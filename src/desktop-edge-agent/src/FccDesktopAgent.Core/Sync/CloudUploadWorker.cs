using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Uploads buffered transactions to the cloud backend in chronological batches.
/// Implements <see cref="ICloudSyncService"/> — called by <see cref="Runtime.CadenceController"/>
/// on each internet-up tick (architecture rule #10: no independent timer loop).
///
/// Architecture guarantees:
/// - Rule #2: Never skip past a failed record — oldest Pending batch is always retried first.
/// - Rule #6: Money values remain long (minor units) throughout serialization.
/// - Suspends automatically when the cadence controller gates on connectivity state.
/// - Decommission state is permanent for the process lifetime; restart required to re-enable.
/// </summary>
public sealed class CloudUploadWorker : ICloudSyncService
{
    private const string UploadPath = "/api/v1/transactions/upload";
    private const string OutcomeAccepted = "ACCEPTED";
    private const string OutcomeDuplicate = "DUPLICATE";
    private const string OutcomeRejected = "REJECTED";
    private const string DecommissionedCode = "DEVICE_DECOMMISSIONED";
    private const string NonLeaderWriteCode = "CONFLICT.NON_LEADER_WRITE";
    private const string StaleLeaderEpochCode = "CONFLICT.STALE_LEADER_EPOCH";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly AuthenticatedCloudRequestHandler _authHandler;
    private readonly IRegistrationManager _registrationManager;
    private readonly IConfigManager _configManager;
    private readonly ILogger<CloudUploadWorker> _logger;

    // Polly retry pipeline for transient HTTP failures (network errors, 5xx responses).
    // 401/403 are handled at the application layer and are NOT retried by this pipeline.
    private readonly ResiliencePipeline _retryPipeline;

    public CloudUploadWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IOptions<AgentConfiguration> config,
        AuthenticatedCloudRequestHandler authHandler,
        IRegistrationManager registrationManager,
        IConfigManager configManager,
        ILogger<CloudUploadWorker> logger)
        : this(scopeFactory, httpFactory, config, authHandler, registrationManager, configManager, logger, retryPipeline: null)
    {
    }

    /// <summary>Internal constructor for testing — allows injecting a custom (e.g. no-op) retry pipeline.</summary>
    internal CloudUploadWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IOptions<AgentConfiguration> config,
        AuthenticatedCloudRequestHandler authHandler,
        IRegistrationManager registrationManager,
        IConfigManager configManager,
        ILogger<CloudUploadWorker> logger,
        ResiliencePipeline? retryPipeline)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _config = config;
        _authHandler = authHandler;
        _registrationManager = registrationManager;
        _configManager = configManager;
        _logger = logger;

        _retryPipeline = retryPipeline ?? new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 5,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = false,
                MaxDelay = TimeSpan.FromSeconds(60),
                // Only retry on transient network/server errors. 401/403 bubble up immediately.
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>(),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Cloud upload HTTP retry {Attempt} after {Delay:g}: {Reason}",
                        args.AttemptNumber + 1, args.RetryDelay, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <inheritdoc />
    public async Task<int> UploadBatchAsync(CancellationToken ct)
    {
        // T-DSK-013: Check centralized decommission flag instead of per-worker volatile boolean.
        if (_registrationManager.IsDecommissioned)
        {
            _logger.LogDebug("Upload skipped: device is decommissioned");
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var bufferManager = scope.ServiceProvider.GetRequiredService<TransactionBufferManager>();
        var config = _config.Value;

        var batch = await bufferManager.GetPendingBatchAsync(config.UploadBatchSize, ct);
        if (batch.Count == 0)
            return 0;

        _logger.LogDebug("Cloud upload: attempting batch of {Count} transaction(s)", batch.Count);

        var request = BuildUploadRequest(batch, config);
        _logger.LogDebug("Upload batch: batchId={BatchId} records={Count}", request.UploadBatchId, batch.Count);

        // DEA-6.2: Enforce HTTPS for cloud communication
        if (!CloudUrlGuard.IsSecure(config.CloudBaseUrl))
        {
            _logger.LogWarning("Cloud upload blocked: CloudBaseUrl {Url} does not use HTTPS", config.CloudBaseUrl);
            return 0;
        }

        // T-DSK-010: Delegate auth flow to the shared handler.
        var authResult = await _authHandler.ExecuteAsync<UploadRequestResult?>(
            (token, innerCt) => SendWithRetryAsync(request, token, config, innerCt),
            "cloud upload", ct);

        // T-DSK-013: RequiresHalt means MarkDecommissionedAsync was already called
        // by AuthenticatedCloudRequestHandler, updating the centralized flag.
        if (authResult.RequiresHalt)
            return 0;

        if (authResult.Outcome == AuthRequestOutcome.NoToken)
            return 0;

        if (authResult.Outcome == AuthRequestOutcome.AuthFailed)
        {
            await RecordBatchFailureAsync(bufferManager, batch, "Token refresh failed after 401", ct);
            return 0;
        }

        if (!authResult.IsSuccess)
        {
            await RecordBatchFailureAsync(bufferManager, batch, authResult.Error?.Message ?? "Request failed", ct);
            return 0;
        }

        var uploadResult = authResult.Value;
        if (uploadResult is null)
        {
            await RecordBatchFailureAsync(bufferManager, batch, "Empty response from cloud", ct);
            return 0;
        }

        if (uploadResult.Outcome == UploadRequestOutcome.AuthoritativeConflict)
        {
            _logger.LogWarning(
                "Cloud upload paused: {ErrorCode} {Message}",
                uploadResult.ErrorCode ?? "CONFLICT",
                uploadResult.Message ?? "authoritative write rejected");
            _configManager.OnPeerDirectoryStale?.Invoke();
            return 0;
        }

        if (uploadResult.Response is null)
        {
            await RecordBatchFailureAsync(bufferManager, batch, "Empty response from cloud", ct);
            return 0;
        }

        var result = await ProcessUploadResponseAsync(bufferManager, batch, uploadResult.Response, ct);

        // F-DSK-045: Persist LastUploadAt after successful upload
        if (result > 0)
        {
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
                var syncState = await db.SyncStates.FindAsync(new object[] { 1 }, ct);
                var now = DateTimeOffset.UtcNow;
                if (syncState is null)
                {
                    syncState = new SyncStateRecord { Id = 1, LastUploadAt = now, UpdatedAt = now };
                    db.SyncStates.Add(syncState);
                }
                else
                {
                    syncState.LastUploadAt = now;
                    syncState.UpdatedAt = now;
                }
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update LastUploadAt (non-fatal)");
            }
        }

        return result;
    }

    // ── HTTP send with Polly retry ────────────────────────────────────────────

    private async Task<UploadRequestResult?> SendWithRetryAsync(
        UploadRequest request,
        string token,
        AgentConfiguration config,
        CancellationToken ct)
    {
        UploadRequestResult? captured = null;
        var url = $"{config.CloudBaseUrl.TrimEnd('/')}{UploadPath}";

        await _retryPipeline.ExecuteAsync(async pipelineCt =>
        {
            // S-DSK-031: Use lowercase "cloud" to match the registered named client
            // with TLS 1.2+ enforcement and certificate pinning.
            var http = _httpFactory.CreateClient("cloud");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            httpRequest.Content = JsonContent.Create(request);

            var httpResponse = await http.SendAsync(httpRequest, pipelineCt);

            PeerDirectoryVersionHelper.CheckAndTrigger(httpResponse, _configManager, _logger);

            // 401 / 403 are application-level decisions — NOT retried by Polly.
            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException();

            if (httpResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                var body = await httpResponse.Content.ReadAsStringAsync(pipelineCt);
                if (body.Contains(DecommissionedCode, StringComparison.OrdinalIgnoreCase))
                    throw new DeviceDecommissionedException(body);

                // Other 403 (e.g. wrong site): surface as HttpRequestException so caller can log
                throw new HttpRequestException($"403 Forbidden: {body}");
            }

            if (httpResponse.StatusCode == HttpStatusCode.Conflict)
            {
                var error = await TryReadErrorAsync(httpResponse, pipelineCt);
                if (string.Equals(error?.ErrorCode, NonLeaderWriteCode, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(error?.ErrorCode, StaleLeaderEpochCode, StringComparison.OrdinalIgnoreCase))
                {
                    captured = UploadRequestResult.AuthoritativeConflict(
                        error?.ErrorCode,
                        error?.Message ?? "authoritative write rejected");
                    return;
                }

                throw new HttpRequestException(error?.Message ?? "409 Conflict");
            }

            // 5xx → HttpRequestException → Polly retries
            httpResponse.EnsureSuccessStatusCode();

            var response = await httpResponse.Content.ReadFromJsonAsync<UploadResponse>(
                cancellationToken: pipelineCt);
            captured = UploadRequestResult.Success(response);
        }, ct);

        return captured;
    }

    // ── Per-record outcome processing ─────────────────────────────────────────

    private async Task<int> ProcessUploadResponseAsync(
        TransactionBufferManager bufferManager,
        IReadOnlyList<BufferedTransaction> batch,
        UploadResponse uploadResponse,
        CancellationToken ct)
    {
        // Build lookup: fccTransactionId → local buffer Id
        // All records in a single batch share the same siteCode, so fccTransactionId alone is unique.
        var lookup = batch.ToDictionary(t => t.FccTransactionId, t => t.Id);

        var acceptedIds = new List<string>();
        var duplicateIds = new List<string>();
        var rejectedIds = new List<string>();

        foreach (var result in uploadResponse.Results)
        {
            if (!lookup.TryGetValue(result.FccTransactionId, out var localId))
            {
                _logger.LogWarning(
                    "Upload response references unknown transaction fccId={FccId}",
                    result.FccTransactionId);
                continue;
            }

            switch (result.Outcome)
            {
                case OutcomeAccepted:
                    acceptedIds.Add(localId);
                    break;

                case OutcomeDuplicate:
                    duplicateIds.Add(localId);
                    _logger.LogDebug(
                        "Transaction {FccId} already known to cloud (duplicate confirmed)",
                        result.FccTransactionId);
                    break;

                case OutcomeRejected:
                    rejectedIds.Add(localId);
                    _logger.LogWarning(
                        "Transaction {FccId} rejected by cloud: [{Code}] {Message}",
                        result.FccTransactionId, result.ErrorCode, result.ErrorMessage);
                    break;

                default:
                    _logger.LogWarning(
                        "Unknown upload outcome '{Outcome}' for transaction {FccId}",
                        result.Outcome, result.FccTransactionId);
                    break;
            }
        }

        int succeeded = 0;

        if (acceptedIds.Count > 0)
        {
            await bufferManager.MarkUploadedAsync(acceptedIds, ct);
            succeeded += acceptedIds.Count;
            _logger.LogInformation("Cloud upload: {Count} transaction(s) accepted", acceptedIds.Count);
        }

        if (duplicateIds.Count > 0)
        {
            await bufferManager.MarkDuplicateConfirmedAsync(duplicateIds, ct);
            succeeded += duplicateIds.Count;
            _logger.LogInformation(
                "Cloud upload: {Count} transaction(s) confirmed as cloud duplicates", duplicateIds.Count);
        }

        if (rejectedIds.Count > 0)
        {
            // REJECTED records stay Pending so they are retried.
            // Architecture rule #2: Never skip past a failed record.
            await bufferManager.RecordUploadFailureAsync(rejectedIds, "REJECTED by cloud", ct);
            _logger.LogWarning("Cloud upload: {Count} transaction(s) rejected by cloud", rejectedIds.Count);

            // GAP-1: Dead-letter records that have exhausted all upload retries.
            // This runs after recording failures so the attempt counts are up to date.
            await bufferManager.DeadLetterExhaustedAsync(ct);
        }

        return succeeded;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task RecordBatchFailureAsync(
        TransactionBufferManager bufferManager,
        IReadOnlyList<BufferedTransaction> batch,
        string error,
        CancellationToken ct)
    {
        var ids = batch.Select(t => t.Id).ToList();
        return bufferManager.RecordUploadFailureAsync(ids, error, ct);
    }

    private static UploadRequest BuildUploadRequest(
        IReadOnlyList<BufferedTransaction> batch,
        AgentConfiguration config)
    {
        var transactions = batch.Select(ToUploadRecord).ToList();
        return new UploadRequest
        {
            Transactions = transactions,
            LeaderEpoch = config.LeaderEpoch > 0 ? config.LeaderEpoch : null,
            UploadBatchId = ComputeStableBatchId(batch, config.LeaderEpoch)
        };
    }

    private static UploadTransactionRecord ToUploadRecord(BufferedTransaction t) => new()
    {
        FccTransactionId = t.FccTransactionId,
        SiteCode = t.SiteCode,
        PumpNumber = t.PumpNumber,
        NozzleNumber = t.NozzleNumber,
        ProductCode = t.ProductCode,
        VolumeMicrolitres = t.VolumeMicrolitres,
        AmountMinorUnits = t.AmountMinorUnits,
        UnitPriceMinorPerLitre = t.UnitPriceMinorPerLitre,
        CurrencyCode = t.CurrencyCode,
        StartedAt = t.StartedAt,
        CompletedAt = t.CompletedAt,
        FccCorrelationId = t.CorrelationId,
        OdooOrderId = t.OdooOrderId,
        FiscalReceiptNumber = t.FiscalReceiptNumber,
        FccVendor = t.FccVendor,
        AttendantId = t.AttendantId,
    };

    private static string ComputeStableBatchId(IReadOnlyList<BufferedTransaction> batch, long leaderEpoch)
    {
        var builder = new StringBuilder();
        builder.Append(leaderEpoch).Append('|');
        foreach (var item in batch.OrderBy(t => t.CompletedAt).ThenBy(t => t.Id, StringComparer.Ordinal))
        {
            builder.Append(item.Id)
                .Append('|')
                .Append(item.FccTransactionId)
                .Append('|')
                .Append(item.SiteCode)
                .Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    private static async Task<ErrorResponse?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }
}

internal enum UploadRequestOutcome
{
    Success,
    AuthoritativeConflict,
}

internal sealed record UploadRequestResult(
    UploadRequestOutcome Outcome,
    UploadResponse? Response = null,
    string? ErrorCode = null,
    string? Message = null)
{
    public static UploadRequestResult Success(UploadResponse? response) =>
        new(UploadRequestOutcome.Success, Response: response);

    public static UploadRequestResult AuthoritativeConflict(string? errorCode, string? message) =>
        new(UploadRequestOutcome.AuthoritativeConflict, ErrorCode: errorCode, Message: message);
}

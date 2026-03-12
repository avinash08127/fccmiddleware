using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Security;
using FccDesktopAgent.Core.Sync.Models;
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

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly IDeviceTokenProvider _tokenProvider;
    private readonly IRegistrationManager _registrationManager;
    private readonly ILogger<CloudUploadWorker> _logger;

    // Polly retry pipeline for transient HTTP failures (network errors, 5xx responses).
    // 401/403 are handled at the application layer and are NOT retried by this pipeline.
    private readonly ResiliencePipeline _retryPipeline;

    // Set permanently on DEVICE_DECOMMISSIONED. Process restart required to clear.
    private volatile bool _decommissioned;

    public CloudUploadWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IOptions<AgentConfiguration> config,
        IDeviceTokenProvider tokenProvider,
        IRegistrationManager registrationManager,
        ILogger<CloudUploadWorker> logger)
        : this(scopeFactory, httpFactory, config, tokenProvider, registrationManager, logger, retryPipeline: null)
    {
    }

    /// <summary>Internal constructor for testing — allows injecting a custom (e.g. no-op) retry pipeline.</summary>
    internal CloudUploadWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IOptions<AgentConfiguration> config,
        IDeviceTokenProvider tokenProvider,
        IRegistrationManager registrationManager,
        ILogger<CloudUploadWorker> logger,
        ResiliencePipeline? retryPipeline)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _config = config;
        _tokenProvider = tokenProvider;
        _registrationManager = registrationManager;
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
        if (_decommissioned)
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

        var token = await _tokenProvider.GetTokenAsync(ct);
        if (token is null)
        {
            _logger.LogWarning("Cloud upload skipped: no device token available (device not yet registered?)");
            return 0;
        }

        // DEA-6.2: Enforce HTTPS for cloud communication
        if (!CloudUrlGuard.IsSecure(config.CloudBaseUrl))
        {
            _logger.LogWarning("Cloud upload blocked: CloudBaseUrl {Url} does not use HTTPS", config.CloudBaseUrl);
            return 0;
        }

        var request = BuildUploadRequest(batch, config);

        UploadResponse? uploadResponse;
        try
        {
            uploadResponse = await SendWithRetryAsync(request, token, config, ct);
        }
        catch (UnauthorizedAccessException)
        {
            // 401: refresh token once and retry the entire batch
            _logger.LogWarning("Cloud upload received 401; refreshing device token");
            try
            {
                token = await _tokenProvider.RefreshTokenAsync(ct);
            }
            catch (RefreshTokenExpiredException rex)
            {
                await _registrationManager.MarkReprovisioningRequiredAsync();
                _decommissioned = true;
                _logger.LogCritical(
                    "REFRESH_TOKEN_EXPIRED: Device requires re-provisioning. " +
                    "Restart the agent to begin provisioning. Reason: {Reason}", rex.Message);
                return 0;
            }
            catch (DeviceDecommissionedException dex)
            {
                await _registrationManager.MarkDecommissionedAsync();
                _decommissioned = true;
                _logger.LogCritical(
                    "DEVICE_DECOMMISSIONED during token refresh. All cloud sync halted. " +
                    "Reason: {Reason}", dex.Message);
                return 0;
            }
            if (token is null)
            {
                _logger.LogWarning("Token refresh failed; recording upload failure for batch");
                await RecordBatchFailureAsync(bufferManager, batch, "Token refresh failed after 401", ct);
                return 0;
            }

            try
            {
                uploadResponse = await SendWithRetryAsync(request, token, config, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Cloud upload failed after token refresh");
                await RecordBatchFailureAsync(bufferManager, batch, ex.Message, ct);
                return 0;
            }
        }
        catch (DeviceDecommissionedException ex)
        {
            await _registrationManager.MarkDecommissionedAsync();
            _decommissioned = true;
            _logger.LogCritical(
                "DEVICE_DECOMMISSIONED received from cloud. All cloud sync halted. " +
                "Agent restart required to re-enable. Reason: {Reason}", ex.Message);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // HTTP failure exhausted all Polly retries
            _logger.LogWarning(ex, "Cloud upload failed after retries for batch of {Count}", batch.Count);
            await RecordBatchFailureAsync(bufferManager, batch, ex.Message, ct);
            return 0;
        }

        if (uploadResponse is null)
        {
            await RecordBatchFailureAsync(bufferManager, batch, "Empty response from cloud", ct);
            return 0;
        }

        return await ProcessUploadResponseAsync(bufferManager, batch, uploadResponse, ct);
    }

    // ── HTTP send with Polly retry ────────────────────────────────────────────

    private async Task<UploadResponse?> SendWithRetryAsync(
        UploadRequest request,
        string token,
        AgentConfiguration config,
        CancellationToken ct)
    {
        UploadResponse? captured = null;
        var url = $"{config.CloudBaseUrl.TrimEnd('/')}{UploadPath}";

        await _retryPipeline.ExecuteAsync(async pipelineCt =>
        {
            var http = _httpFactory.CreateClient("Cloud");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            httpRequest.Content = JsonContent.Create(request);

            var httpResponse = await http.SendAsync(httpRequest, pipelineCt);

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

            // 5xx → HttpRequestException → Polly retries
            httpResponse.EnsureSuccessStatusCode();

            captured = await httpResponse.Content.ReadFromJsonAsync<UploadResponse>(
                cancellationToken: pipelineCt);
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
        var transactions = batch.Select(t => ToCanonical(t, config)).ToList();
        return new UploadRequest { Transactions = transactions };
    }

    private static CanonicalTransaction ToCanonical(BufferedTransaction t, AgentConfiguration config) => new()
    {
        Id = t.Id,
        FccTransactionId = t.FccTransactionId,
        SiteCode = t.SiteCode,
        LegalEntityId = !string.IsNullOrWhiteSpace(config.LegalEntityId) ? config.LegalEntityId : config.SiteId,
        PumpNumber = t.PumpNumber,
        NozzleNumber = t.NozzleNumber,
        ProductCode = t.ProductCode,
        VolumeMicrolitres = t.VolumeMicrolitres,
        AmountMinorUnits = t.AmountMinorUnits,
        UnitPriceMinorPerLitre = t.UnitPriceMinorPerLitre,
        CurrencyCode = t.CurrencyCode,
        StartedAt = t.StartedAt,
        CompletedAt = t.CompletedAt,
        FiscalReceiptNumber = t.FiscalReceiptNumber,
        FccVendor = t.FccVendor,
        AttendantId = t.AttendantId,
        IngestionSource = t.IngestionSource,
        RawPayloadJson = t.RawPayloadJson,
        CorrelationId = t.CorrelationId,
        SchemaVersion = t.SchemaVersion,
        IngestedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
    };
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Polls <c>GET /api/v1/transactions/synced-status</c> and advances local buffer records
/// from <see cref="FccDesktopAgent.Core.Adapter.Common.SyncStatus.Uploaded"/> →
/// <see cref="FccDesktopAgent.Core.Adapter.Common.SyncStatus.SyncedToOdoo"/>.
///
/// Implements <see cref="ISyncedToOdooPoller"/> — called by <see cref="Runtime.CadenceController"/>
/// on each internet-up tick (architecture rule #10: no independent timer loop).
///
/// Architecture guarantees:
/// - Suspends automatically when the cadence controller gates on connectivity state.
/// - Decommission state is permanent for the process lifetime; restart required to re-enable.
/// - SyncedToOdoo records are excluded from local API responses (handled in <see cref="TransactionBufferManager"/>).
/// </summary>
public sealed class StatusPollWorker : ISyncedToOdooPoller
{
    private const string StatusPollPath = "/api/v1/transactions/synced-status";
    private const string DecommissionedCode = "DEVICE_DECOMMISSIONED";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly AuthenticatedCloudRequestHandler _authHandler;
    private readonly IRegistrationManager _registrationManager;
    private readonly IConfigManager _configManager;
    private readonly ILogger<StatusPollWorker> _logger;

    public StatusPollWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IOptions<AgentConfiguration> config,
        AuthenticatedCloudRequestHandler authHandler,
        IRegistrationManager registrationManager,
        IConfigManager configManager,
        ILogger<StatusPollWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _config = config;
        _authHandler = authHandler;
        _registrationManager = registrationManager;
        _configManager = configManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> PollAsync(CancellationToken ct)
    {
        // T-DSK-013: Check centralized decommission flag instead of per-worker volatile boolean.
        if (_registrationManager.IsDecommissioned)
        {
            _logger.LogDebug("Status poll skipped: device is decommissioned");
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var bufferManager = scope.ServiceProvider.GetRequiredService<TransactionBufferManager>();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var since = await DetermineSinceAsync(db, ct);

        _logger.LogDebug("SYNCED_TO_ODOO poll: querying since {Since:O}", since);

        // T-DSK-010: Delegate auth flow to the shared handler.
        var result = await _authHandler.ExecuteAsync<SyncedStatusResponse?>(
            (token, innerCt) => SendPollRequestAsync(since, token, innerCt),
            "status poll", ct);

        if (result.RequiresHalt)
            return 0;

        if (!result.IsSuccess)
            return 0;

        var response = result.Value;
        if (response is null || response.FccTransactionIds.Count == 0)
        {
            // Record the successful poll time for telemetry/visibility, but never use it
            // as the sole source of truth for the next query lower bound.
            await UpdateLastStatusSyncAtAsync(db, DateTimeOffset.UtcNow, ct);
            return 0;
        }

        _logger.LogDebug(
            "Status poll: {Count} transaction(s) reported as SYNCED_TO_ODOO by cloud",
            response.FccTransactionIds.Count);

        var advanced = await bufferManager.MarkSyncedToOdooAsync(response.FccTransactionIds, ct);

        await UpdateLastStatusSyncAtAsync(db, DateTimeOffset.UtcNow, ct);

        if (advanced > 0)
            _logger.LogInformation(
                "Status poll: {Count} record(s) advanced to SyncedToOdoo", advanced);

        return advanced;
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────

    private async Task<SyncedStatusResponse?> SendPollRequestAsync(
        DateTimeOffset since,
        string token,
        CancellationToken ct)
    {
        var config = _config.Value;
        var sinceParam = Uri.EscapeDataString(since.UtcDateTime.ToString("O"));
        var url = $"{config.CloudBaseUrl.TrimEnd('/')}{StatusPollPath}?since={sinceParam}";

        var http = _httpFactory.CreateClient("cloud");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request, ct);

        PeerDirectoryVersionHelper.CheckAndTrigger(response, _configManager, _logger);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException();

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains(DecommissionedCode, StringComparison.OrdinalIgnoreCase))
                throw new DeviceDecommissionedException(body);

            throw new HttpRequestException($"403 Forbidden: {body}");
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SyncedStatusResponse>(cancellationToken: ct)
            ?? new SyncedStatusResponse { FccTransactionIds = [] };
    }

    // ── SyncState helpers ─────────────────────────────────────────────────────

    private static async Task<DateTimeOffset> DetermineSinceAsync(AgentDbContext db, CancellationToken ct)
    {
        var state = await db.SyncStates.FindAsync([1], ct);
        var lastSuccessfulPollAt = state?.LastStatusSyncAt ?? DateTimeOffset.UtcNow.AddDays(-1);

        var oldestOutstandingUploadAt = await db.Transactions
            .AsNoTracking()
            .Where(t => t.SyncStatus == Adapter.Common.SyncStatus.Uploaded)
            .Select(t => (DateTimeOffset?)(t.LastUploadAttemptAt ?? t.CompletedAt))
            .OrderBy(timestamp => timestamp)
            .FirstOrDefaultAsync(ct);

        if (!oldestOutstandingUploadAt.HasValue)
            return lastSuccessfulPollAt;

        return oldestOutstandingUploadAt.Value < lastSuccessfulPollAt
            ? oldestOutstandingUploadAt.Value
            : lastSuccessfulPollAt;
    }

    private static async Task UpdateLastStatusSyncAtAsync(
        AgentDbContext db,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var state = await db.SyncStates.FindAsync([1], ct);
        if (state is null)
        {
            state = new SyncStateRecord { Id = 1, LastStatusSyncAt = now, UpdatedAt = now };
            db.SyncStates.Add(state);
        }
        else
        {
            state.LastStatusSyncAt = now;
            state.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Sync.Models;
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
    private readonly IDeviceTokenProvider _tokenProvider;
    private readonly IRegistrationManager _registrationManager;
    private readonly ILogger<StatusPollWorker> _logger;

    // Set permanently on DEVICE_DECOMMISSIONED. Process restart required to clear.
    private volatile bool _decommissioned;

    public StatusPollWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IOptions<AgentConfiguration> config,
        IDeviceTokenProvider tokenProvider,
        IRegistrationManager registrationManager,
        ILogger<StatusPollWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _config = config;
        _tokenProvider = tokenProvider;
        _registrationManager = registrationManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> PollAsync(CancellationToken ct)
    {
        if (_decommissioned)
        {
            _logger.LogDebug("Status poll skipped: device is decommissioned");
            return 0;
        }

        var token = await _tokenProvider.GetTokenAsync(ct);
        if (token is null)
        {
            _logger.LogWarning("Status poll skipped: no device token available (device not yet registered?)");
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var bufferManager = scope.ServiceProvider.GetRequiredService<TransactionBufferManager>();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var since = await GetLastStatusSyncAtAsync(db, ct);

        _logger.LogDebug("SYNCED_TO_ODOO poll: querying since {Since:O}", since);

        SyncedStatusResponse? response;
        try
        {
            response = await SendPollRequestAsync(since, token, ct);
        }
        catch (UnauthorizedAccessException)
        {
            // 401: refresh token once and retry
            _logger.LogWarning("Status poll received 401; refreshing device token");
            token = await _tokenProvider.RefreshTokenAsync(ct);
            if (token is null)
            {
                _logger.LogWarning("Token refresh failed; status poll aborted");
                return 0;
            }

            try
            {
                response = await SendPollRequestAsync(since, token, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Status poll failed after token refresh");
                return 0;
            }
        }
        catch (DeviceDecommissionedException ex)
        {
            await _registrationManager.MarkDecommissionedAsync();
            _decommissioned = true;
            _logger.LogCritical(
                "DEVICE_DECOMMISSIONED received from cloud during status poll. " +
                "All cloud sync halted. Agent restart required to re-enable. Reason: {Reason}", ex.Message);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Status poll HTTP request failed");
            return 0;
        }

        if (response is null || response.FccTransactionIds.Count == 0)
        {
            // No new synced records — advance the timestamp so the next poll window moves forward.
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

        return await response.Content.ReadFromJsonAsync<SyncedStatusResponse>(cancellationToken: ct);
    }

    // ── SyncState helpers ─────────────────────────────────────────────────────

    private static async Task<DateTimeOffset> GetLastStatusSyncAtAsync(AgentDbContext db, CancellationToken ct)
    {
        var state = await db.SyncStates.FindAsync([1], ct);
        // On first run fall back to 24 hours ago to catch any recently synced records.
        return state?.LastStatusSyncAt ?? DateTimeOffset.UtcNow.AddDays(-1);
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

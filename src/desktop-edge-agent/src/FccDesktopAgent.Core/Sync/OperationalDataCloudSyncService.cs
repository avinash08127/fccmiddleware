using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FccDesktopAgent.Core.Adapter;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Captures auxiliary FCC operational data locally and uploads pending batches to cloud site-data endpoints.
/// </summary>
public sealed class OperationalDataCloudSyncService : IOperationalDataCloudSyncService
{
    private const int MaxBatchSize = 100;
    private const string DecommissionedCode = "DEVICE_DECOMMISSIONED";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFccAdapterFactory _adapterFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly IConnectivityMonitor _connectivity;
    private readonly AuthenticatedCloudRequestHandler _authHandler;
    private readonly IRegistrationManager _registrationManager;
    private readonly IConfigManager _configManager;
    private readonly OperationalDataEventSink _eventSink;
    private readonly ILogger<OperationalDataCloudSyncService> _logger;

    public OperationalDataCloudSyncService(
        IServiceScopeFactory scopeFactory,
        IFccAdapterFactory adapterFactory,
        IHttpClientFactory httpFactory,
        IOptionsMonitor<AgentConfiguration> config,
        IConnectivityMonitor connectivity,
        AuthenticatedCloudRequestHandler authHandler,
        IRegistrationManager registrationManager,
        IConfigManager configManager,
        OperationalDataEventSink eventSink,
        ILogger<OperationalDataCloudSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _adapterFactory = adapterFactory;
        _httpFactory = httpFactory;
        _config = config;
        _connectivity = connectivity;
        _authHandler = authHandler;
        _registrationManager = registrationManager;
        _configManager = configManager;
        _eventSink = eventSink;
        _logger = logger;
    }

    public async Task<OperationalDataSyncResult> SyncAsync(CancellationToken ct)
    {
        if (_registrationManager.IsDecommissioned)
            return OperationalDataSyncResult.Empty();

        var config = _config.CurrentValue;
        if (string.IsNullOrWhiteSpace(config.SiteId) || !CloudUrlGuard.IsSecure(config.CloudBaseUrl))
            return OperationalDataSyncResult.Empty();

        var captured = 0;
        if (_connectivity.Current.IsFccUp)
            captured = await CaptureOperationalDataAsync(config, ct);

        var uploaded = await UploadPendingDataAsync(config, ct);
        return new OperationalDataSyncResult(captured, uploaded);
    }

    private async Task<int> CaptureOperationalDataAsync(AgentConfiguration config, CancellationToken ct)
    {
        var siteConfig = _configManager.CurrentSiteConfig;
        if (siteConfig is null)
            return 0;

        ResolvedFccRuntimeConfiguration resolvedConfig;
        try
        {
            resolvedConfig = DesktopFccRuntimeConfiguration.Resolve(
                config,
                siteConfig,
                TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Operational data capture skipped: FCC runtime configuration is not ready");
            return 0;
        }

        var adapter = _adapterFactory.Create(resolvedConfig.Vendor, resolvedConfig.ConnectionConfig);
        var lifecycle = adapter as IFccConnectionLifecycle;
        var connectedHere = false;

        try
        {
            if (lifecycle is not null)
            {
                lifecycle.SetEventListener(_eventSink);
                if (!lifecycle.IsConnected)
                {
                    await lifecycle.ConnectAsync(ct);
                    connectedHere = true;
                }
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
            var now = DateTimeOffset.UtcNow;
            var captured = 0;

            if (config.TotalsEnabled
                && adapter is IFccTotalsProvider totalsProvider
                && await ShouldCaptureTotalsAsync(db, config, now, ct))
            {
                var totals = await totalsProvider.GetPumpTotalsAsync(ct);
                if (totals.Count > 0)
                {
                    db.PumpTotalsSnapshots.AddRange(totals.Select(total => new BufferedPumpTotalsSnapshot
                    {
                        PumpNumber = total.PumpNumber,
                        TotalVolumeMicrolitres = total.TotalVolumeMicrolitres,
                        TotalAmountMinorUnits = total.TotalAmountMinorUnits,
                        CurrencyCode = total.CurrencyCode,
                        ObservedAtUtc = total.ObservedAtUtc,
                        IsSynced = false
                    }));
                    captured += totals.Count;
                }
            }

            var priceSnapshotRequested = _eventSink.ConsumePriceSnapshotRequested();
            if (config.PriceManagementEnabled
                && config.PriceSyncToCloud
                && adapter is IFccPriceManagement priceManagement
                && await ShouldCapturePricesAsync(db, config, now, priceSnapshotRequested, ct))
            {
                var snapshot = await priceManagement.GetCurrentPricesAsync(ct);
                if (snapshot?.Grades.Count > 0)
                {
                    db.PriceSnapshots.AddRange(snapshot.Grades.Select(grade => new BufferedPriceSnapshot
                    {
                        PriceSetId = snapshot.PriceSetId,
                        GradeId = grade.GradeId,
                        GradeName = grade.GradeName ?? grade.GradeId,
                        PriceMinorUnits = grade.PriceMinorUnits,
                        CurrencyCode = grade.CurrencyCode,
                        ObservedAtUtc = snapshot.ObservedAtUtc,
                        IsSynced = false
                    }));
                    captured += snapshot.Grades.Count;
                }
            }

            if (captured > 0)
                await db.SaveChangesAsync(ct);

            return captured;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Operational data capture failed");
            return 0;
        }
        finally
        {
            if (connectedHere && lifecycle is not null)
            {
                try
                {
                    await lifecycle.DisconnectAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Ignoring operational-data lifecycle disconnect failure");
                }
            }
        }
    }

    private async Task<int> UploadPendingDataAsync(AgentConfiguration config, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var uploaded = 0;
        uploaded += await UploadBnaReportsAsync(db, config, ct);
        uploaded += await UploadPumpTotalsAsync(db, config, ct);
        uploaded += await UploadPumpControlHistoryAsync(db, config, ct);
        uploaded += await UploadPriceSnapshotsAsync(db, config, ct);
        return uploaded;
    }

    private async Task<int> UploadBnaReportsAsync(AgentDbContext db, AgentConfiguration config, CancellationToken ct)
    {
        if (!config.BnaReportingEnabled)
            return 0;

        var pending = await db.BnaReports
            .Where(item => !item.IsSynced)
            .OrderBy(item => item.ReportedAtUtc)
            .Take(MaxBatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return 0;

        var request = new BnaReportBatchUpload
        {
            Reports = pending.Select(item => new BnaReportUploadItem
            {
                TerminalId = item.TerminalId,
                NotesAccepted = item.NotesAccepted,
                ReportedAtUtc = item.ReportedAtUtc
            }).ToList()
        };

        var accepted = await SendSiteDataBatchAsync(
            $"/api/v1/sites/{Uri.EscapeDataString(config.SiteId)}/bna-reports",
            request,
            "BNA report upload",
            pending.Count,
            config,
            ct);

        if (accepted <= 0)
            return 0;

        foreach (var item in pending)
            item.IsSynced = true;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Operational data upload: {Count} BNA report(s) uploaded", accepted);
        return accepted;
    }

    private async Task<int> UploadPumpTotalsAsync(AgentDbContext db, AgentConfiguration config, CancellationToken ct)
    {
        var pending = await db.PumpTotalsSnapshots
            .Where(item => !item.IsSynced)
            .OrderBy(item => item.ObservedAtUtc)
            .ThenBy(item => item.Id)
            .Take(MaxBatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return 0;

        var request = new PumpTotalsBatchUpload
        {
            Totals = pending.Select(item => new PumpTotalsUploadItem
            {
                PumpNumber = item.PumpNumber,
                TotalVolumeMicrolitres = item.TotalVolumeMicrolitres,
                TotalAmountMinorUnits = item.TotalAmountMinorUnits,
                ObservedAtUtc = item.ObservedAtUtc
            }).ToList()
        };

        var accepted = await SendSiteDataBatchAsync(
            $"/api/v1/sites/{Uri.EscapeDataString(config.SiteId)}/pump-totals",
            request,
            "pump totals upload",
            pending.Count,
            config,
            ct);

        if (accepted <= 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        foreach (var item in pending)
        {
            item.IsSynced = true;
            item.SyncedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Operational data upload: {Count} pump totals snapshot(s) uploaded", accepted);
        return accepted;
    }

    private async Task<int> UploadPumpControlHistoryAsync(AgentDbContext db, AgentConfiguration config, CancellationToken ct)
    {
        var pending = await db.PumpBlockHistory
            .Where(item => !item.IsSynced)
            .OrderBy(item => item.Timestamp)
            .ThenBy(item => item.Id)
            .Take(MaxBatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return 0;

        var request = new PumpControlHistoryBatchUpload
        {
            Events = pending.Select(item => new PumpControlHistoryUploadItem
            {
                PumpNumber = item.FpId,
                ActionType = item.ActionType,
                Source = item.Source,
                Note = string.IsNullOrWhiteSpace(item.Note) ? null : item.Note,
                ActionAtUtc = item.Timestamp
            }).ToList()
        };

        var accepted = await SendSiteDataBatchAsync(
            $"/api/v1/sites/{Uri.EscapeDataString(config.SiteId)}/pump-control-history",
            request,
            "pump control history upload",
            pending.Count,
            config,
            ct);

        if (accepted <= 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        foreach (var item in pending)
        {
            item.IsSynced = true;
            item.SyncedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Operational data upload: {Count} pump control event(s) uploaded", accepted);
        return accepted;
    }

    private async Task<int> UploadPriceSnapshotsAsync(AgentDbContext db, AgentConfiguration config, CancellationToken ct)
    {
        if (!config.PriceSyncToCloud)
            return 0;

        var pending = await db.PriceSnapshots
            .Where(item => !item.IsSynced)
            .OrderBy(item => item.ObservedAtUtc)
            .ThenBy(item => item.Id)
            .Take(MaxBatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return 0;

        var request = new PriceSnapshotBatchUpload
        {
            Snapshots = pending.Select(item => new PriceSnapshotUploadItem
            {
                PriceSetId = item.PriceSetId,
                GradeId = item.GradeId,
                GradeName = item.GradeName,
                PriceMinorUnits = item.PriceMinorUnits,
                CurrencyCode = item.CurrencyCode,
                ObservedAtUtc = item.ObservedAtUtc
            }).ToList()
        };

        var accepted = await SendSiteDataBatchAsync(
            $"/api/v1/sites/{Uri.EscapeDataString(config.SiteId)}/price-snapshots",
            request,
            "price snapshot upload",
            pending.Count,
            config,
            ct);

        if (accepted <= 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        foreach (var item in pending)
        {
            item.IsSynced = true;
            item.SyncedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Operational data upload: {Count} price snapshot(s) uploaded", accepted);
        return accepted;
    }

    private async Task<int> SendSiteDataBatchAsync<TRequest>(
        string relativePath,
        TRequest request,
        string operationName,
        int expectedCount,
        AgentConfiguration config,
        CancellationToken ct)
    {
        var result = await _authHandler.ExecuteAsync<int>(
            (token, innerCt) => SendSiteDataBatchInternalAsync(relativePath, request, token, config, expectedCount, innerCt),
            operationName,
            ct);

        if (!result.IsSuccess || result.RequiresHalt)
            return 0;

        return result.Value;
    }

    private async Task<int> SendSiteDataBatchInternalAsync<TRequest>(
        string relativePath,
        TRequest request,
        string token,
        AgentConfiguration config,
        int expectedCount,
        CancellationToken ct)
    {
        var url = $"{config.CloudBaseUrl.TrimEnd('/')}{relativePath}";
        var http = _httpFactory.CreateClient("cloud");

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(message, ct);

        PeerDirectoryVersionHelper.CheckAndTrigger(response, _configManager, _logger);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException();

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var error = await TryReadErrorAsync(response, ct);
            if (string.Equals(error?.ErrorCode, DecommissionedCode, StringComparison.OrdinalIgnoreCase))
                throw new DeviceDecommissionedException(error?.Message ?? "Device decommissioned");

            throw new HttpRequestException(error?.Message ?? "403 Forbidden");
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await TryReadErrorAsync(response, ct);
            throw new HttpRequestException(error?.Message ?? $"Site-data upload failed with {(int)response.StatusCode}");
        }

        var accepted = await response.Content.ReadFromJsonAsync<SiteDataAcceptedResponse>(cancellationToken: ct);
        return accepted?.Count ?? expectedCount;
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

    private static async Task<bool> ShouldCaptureTotalsAsync(
        AgentDbContext db,
        AgentConfiguration config,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var lastObservedAt = await db.PumpTotalsSnapshots
            .OrderByDescending(item => item.ObservedAtUtc)
            .Select(item => (DateTimeOffset?)item.ObservedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (!lastObservedAt.HasValue)
            return true;

        var intervalSeconds = config.TotalsPollIntervalSeconds > 0 ? config.TotalsPollIntervalSeconds : 300;
        return now - lastObservedAt.Value >= TimeSpan.FromSeconds(intervalSeconds);
    }

    private static async Task<bool> ShouldCapturePricesAsync(
        AgentDbContext db,
        AgentConfiguration config,
        DateTimeOffset now,
        bool forceRefresh,
        CancellationToken ct)
    {
        if (forceRefresh)
            return true;

        var lastObservedAt = await db.PriceSnapshots
            .OrderByDescending(item => item.ObservedAtUtc)
            .Select(item => (DateTimeOffset?)item.ObservedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (!lastObservedAt.HasValue)
            return true;

        var intervalSeconds = config.TotalsPollIntervalSeconds > 0
            ? config.TotalsPollIntervalSeconds
            : Math.Max(config.CloudSyncIntervalSeconds, 60);

        return now - lastObservedAt.Value >= TimeSpan.FromSeconds(intervalSeconds);
    }
}

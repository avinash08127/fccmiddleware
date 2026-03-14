using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using FccDesktopAgent.Core.Ingestion;
using FccDesktopAgent.Core.PreAuth;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Runtime;

/// <summary>
/// Single coalesced cadence controller for all recurring runtime work.
///
/// Architecture rule #10: ONE cadence controller. No independent timer loops.
///
/// Each tick dispatches (gated by current connectivity state):
///   1. FCC poll           — if FCC is reachable and mode is Relay or BufferAlways
///   2. Cloud upload       — if internet is up
///   3. SYNCED_TO_ODOO poll — piggybacked on cloud-capable cycles (every N ticks)
///   4. Config poll        — piggybacked on cloud-capable cycles (every N ticks per config interval)
///   5. Telemetry report   — every N ticks based on configured telemetry interval
///
/// Responds to <see cref="IConnectivityMonitor.StateChanged"/> events for immediate triggers:
///   - Any → FullyOnline: wakes the loop immediately to start buffer replay and status sync.
///
/// Workers that are not yet registered in DI are resolved optionally and silently skipped.
/// </summary>
public sealed class CadenceController : BackgroundService
{
    private readonly IConnectivityMonitor _connectivity;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly ILogger<CadenceController> _logger;
    private readonly IRegistrationManager? _registrationManager;

    // Optional workers — resolved at construction time; null if not yet registered.
    private readonly IIngestionOrchestrator? _ingestion;
    private readonly ICloudSyncService? _cloudSync;
    private readonly IAgentCommandPoller? _commandPoller;
    private readonly ISyncedToOdooPoller? _syncedToOdooPoller;
    private readonly IConfigPoller? _configPoller;
    private readonly IConfigManager? _configManager;
    private readonly ITelemetryReporter? _telemetryReporter;
    private readonly IVersionChecker? _versionChecker;

    // Wake-up gate: signalled by state-change events so the loop runs an immediate cycle
    // rather than waiting for the full tick interval. Bounded at 1 to avoid queuing.
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    // Scope factory used to create scoped services (e.g. IPreAuthHandler) per tick.
    private readonly IServiceScopeFactory _scopeFactory;

    // Monotonic tick counter for sub-interval scheduling (telemetry, status poll, config poll).
    private int _tick;

    // Version compatibility flag. Set to false when cloud reports this agent version
    // is below minimum supported. When false, FCC communication is disabled.
    // Defaults to true (fail-open) — FCC is allowed until explicit incompatibility is detected.
    private volatile bool _versionCompatible = true;

    public CadenceController(
        IConnectivityMonitor connectivity,
        IOptionsMonitor<AgentConfiguration> config,
        ILogger<CadenceController> logger,
        IServiceProvider services)
    {
        _connectivity = connectivity;
        _config = config;
        _logger = logger;
        _registrationManager = (IRegistrationManager?)services.GetService(typeof(IRegistrationManager));
        _ingestion = (IIngestionOrchestrator?)services.GetService(typeof(IIngestionOrchestrator));
        _cloudSync = (ICloudSyncService?)services.GetService(typeof(ICloudSyncService));
        _commandPoller = (IAgentCommandPoller?)services.GetService(typeof(IAgentCommandPoller));
        _syncedToOdooPoller = (ISyncedToOdooPoller?)services.GetService(typeof(ISyncedToOdooPoller));
        _configPoller = (IConfigPoller?)services.GetService(typeof(IConfigPoller));
        _configManager = (IConfigManager?)services.GetService(typeof(IConfigManager));
        _telemetryReporter = (ITelemetryReporter?)services.GetService(typeof(ITelemetryReporter));
        _versionChecker = (IVersionChecker?)services.GetService(typeof(IVersionChecker));
        _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CadenceController started");

        // Load last-known-good config from database before starting the cadence loop.
        if (_configManager is not null)
        {
            try
            {
                await _configManager.LoadFromDatabaseAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to load config from database on startup");
            }
        }

        // PN-4.1 + PN-3.4: Ensure push-mode adapters (e.g. Petronite webhook listener)
        // are started early, before the main cadence loop begins. This is independent of
        // FCC connectivity state — push listeners are local HTTP servers that must be ready
        // to receive callbacks as soon as the agent boots.
        if (_ingestion is not null)
        {
            try
            {
                await _ingestion.EnsurePushListenersInitializedAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Push listener initialization failed on startup (non-fatal)");
            }
        }

        // Per requirements §15.13: check version compatibility on startup.
        // If below minimum version, disable FCC communication to prevent data format mismatches.
        await PerformStartupVersionCheckAsync(stoppingToken);

        _connectivity.StateChanged += OnConnectivityStateChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunCycleAsync(stoppingToken);
                await WaitForNextTickAsync(stoppingToken);
                _tick++;
            }
        }
        finally
        {
            _connectivity.StateChanged -= OnConnectivityStateChanged;
            _wakeSignal.Dispose();
        }

        _logger.LogInformation("CadenceController stopped");
    }

    // ── Per-tick work dispatching ─────────────────────────────────────────────

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var snapshot = _connectivity.Current;
        var config = _config.CurrentValue;

        if (_registrationManager is not null && !_registrationManager.IsRegistered)
        {
            _logger.LogDebug("CadenceController tick {Tick}: runtime work skipped because device is not registered", _tick);
            return;
        }

        if (_registrationManager?.IsDecommissioned == true)
        {
            _logger.LogDebug("CadenceController tick {Tick}: runtime work skipped because device is decommissioned", _tick);
            return;
        }

        _logger.LogDebug(
            "CadenceController tick {Tick}: state={State} mode={Mode} internet={Internet} fcc={Fcc}",
            _tick, snapshot.State, config.IngestionMode, snapshot.IsInternetUp, snapshot.IsFccUp);

        // ── FCC Poll ─────────────────────────────────────────────────────────
        // Only when FCC is reachable AND ingestion mode requires polling
        // AND agent version is compatible (per §15.13).
        if (!_versionCompatible)
        {
            _logger.LogDebug(
                "FCC poll skipped (tick {Tick}): agent version below minimum — FCC communication disabled", _tick);
        }
        else if (snapshot.IsFccUp && ShouldPollFcc(config))
        {
            if (_ingestion is not null)
            {
                try
                {
                    var result = await _ingestion.PollAndBufferAsync(ct);
                    if (result.NewTransactionsBuffered > 0 || result.DuplicatesSkipped > 0)
                    {
                        _logger.LogDebug(
                            "FCC poll tick {Tick}: {New} buffered, {Dup} duplicates skipped",
                            _tick, result.NewTransactionsBuffered, result.DuplicatesSkipped);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "FCC poll failed on tick {Tick}", _tick);
                }
            }
            else
            {
                _logger.LogDebug("FCC poll tick {Tick}: ingestion orchestrator not registered in DI — skipping", _tick);
            }
        }
        else if (!snapshot.IsFccUp)
        {
            _logger.LogDebug("FCC poll skipped (tick {Tick}): FCC unreachable — state={State}", _tick, snapshot.State);
        }

        // ── Cloud Upload ──────────────────────────────────────────────────────
        // Only when internet is up. Never skip past a failed record (CloudSyncService handles ordering).
        if (snapshot.IsInternetUp)
        {
            if (_commandPoller is not null)
            {
                try
                {
                    var commandResult = await _commandPoller.PollAsync(ct);
                    if (commandResult.CommandCount > 0 || commandResult.AckedCount > 0)
                    {
                        _logger.LogInformation(
                            "Command poll tick {Tick}: {Commands} command(s), {Acked} ack(s)",
                            _tick, commandResult.CommandCount, commandResult.AckedCount);
                    }

                    if (commandResult.HaltRequested)
                    {
                        _logger.LogWarning("Command poll tick {Tick}: runtime halt requested", _tick);
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Command poll failed on tick {Tick}", _tick);
                }
            }

            if (_cloudSync is not null)
            {
                try
                {
                    var uploaded = await _cloudSync.UploadBatchAsync(ct);
                    if (uploaded > 0)
                        _logger.LogDebug("Cloud upload tick {Tick}: {Count} transactions uploaded", _tick, uploaded);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Cloud upload failed on tick {Tick}", _tick);
                }
            }
            else
            {
                _logger.LogDebug("Cloud upload tick {Tick}: cloud sync service not registered in DI — skipping", _tick);
            }

            // ── SYNCED_TO_ODOO Status Poll ────────────────────────────────────
            // Coalesced under the same internet-up check. Runs every N ticks per config.
            // Architecture rule #10: coalesced with cloud health checks in this loop.
            if (IsSyncedToOdooPollTick(config))
            {
                if (_syncedToOdooPoller is not null)
                {
                    try
                    {
                        var synced = await _syncedToOdooPoller.PollAsync(ct);
                        if (synced > 0)
                            _logger.LogInformation(
                                "SYNCED_TO_ODOO poll tick {Tick}: {Count} record(s) advanced", _tick, synced);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "SYNCED_TO_ODOO poll failed on tick {Tick}", _tick);
                    }
                }
                else
                {
                    _logger.LogDebug("SYNCED_TO_ODOO poll tick {Tick}: poller not registered in DI — skipping", _tick);
                }
            }

            // ── Config Poll ───────────────────────────────────────────────────
            // Coalesced under internet-up check. Runs every N ticks per configPollIntervalSeconds.
            if (IsConfigPollTick(config))
            {
                if (_configPoller is not null)
                {
                    try
                    {
                        var applied = await _configPoller.PollAsync(ct);
                        if (applied)
                            _logger.LogInformation(
                                "Config poll tick {Tick}: new configuration applied", _tick);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Config poll failed on tick {Tick}", _tick);
                    }
                }
                else
                {
                    _logger.LogDebug("Config poll tick {Tick}: config poller not registered in DI — skipping", _tick);
                }
            }

            // ── Telemetry ─────────────────────────────────────────────────────
            if (IsTelemetryTick(config))
            {
                if (_telemetryReporter is not null)
                {
                    try
                    {
                        var sent = await _telemetryReporter.ReportAsync(ct);
                        if (sent)
                            _logger.LogDebug("Telemetry report tick {Tick}: sent successfully", _tick);
                        else
                            _logger.LogDebug("Telemetry report tick {Tick}: skipped (send failed or no token)", _tick);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Telemetry report failed on tick {Tick}", _tick);
                    }
                }
                else
                {
                    _logger.LogDebug("Telemetry report tick {Tick}: telemetry reporter not registered in DI — skipping", _tick);
                }
            }
        }
        else
        {
            _logger.LogDebug(
                "Cloud work skipped (tick {Tick}): internet down — state={State}", _tick, snapshot.State);
        }

        // ── Pre-Auth Expiry Check ─────────────────────────────────────────────
        // Runs unconditionally every tick (regardless of internet state).
        // Uses a dedicated scope because IPreAuthHandler depends on AgentDbContext (scoped).
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var preAuthHandler = scope.ServiceProvider.GetService<IPreAuthHandler>();
            if (preAuthHandler is not null)
            {
                var expired = await preAuthHandler.RunExpiryCheckAsync(ct);
                if (expired > 0)
                    _logger.LogInformation("Pre-auth expiry tick {Tick}: expired {Count} record(s)", _tick, expired);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pre-auth expiry check failed on tick {Tick}", _tick);
        }
    }

    // ── Tick pacing with wake-up support ─────────────────────────────────────

    private async Task WaitForNextTickAsync(CancellationToken ct)
    {
        var config = _config.CurrentValue;
        var baseInterval = TimeSpan.FromSeconds(
            config.CloudSyncIntervalSeconds > 0 ? config.CloudSyncIntervalSeconds : 30);

        // ±20% jitter per architecture rule to prevent synchronized bursts across devices.
        var jitterMs = (int)(baseInterval.TotalMilliseconds * 0.2);
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(-jitterMs, jitterMs));
        var delay = baseInterval + jitter;

        try
        {
            // Wait for the tick interval OR an immediate wake-up signal (e.g. FullyOnline recovery).
            await Task.WhenAny(
                Task.Delay(delay, ct),
                _wakeSignal.WaitAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Let stoppingToken cancellation propagate cleanly.
        }
    }

    // ── Connectivity state-change handler ─────────────────────────────────────

    private void OnConnectivityStateChanged(object? sender, ConnectivitySnapshot snapshot)
    {
        // Log cadence-layer side effects per spec §5.4 transition table.
        switch (snapshot.State)
        {
            case ConnectivityState.InternetDown:
                _logger.LogWarning(
                    "[CADENCE] Internet DOWN — cloud upload and SYNCED_TO_ODOO polling suspended");
                break;

            case ConnectivityState.FccUnreachable:
                _logger.LogWarning(
                    "[CADENCE] FCC unreachable — FCC polling suspended; alert diagnostics UI");
                break;

            case ConnectivityState.FullyOffline:
                _logger.LogWarning(
                    "[CADENCE] Fully offline — all cloud and FCC work suspended; local API continues serving buffer");
                break;

            case ConnectivityState.FullyOnline:
                _logger.LogInformation(
                    "[CADENCE] Fully online — waking cadence loop for immediate buffer replay and status sync");
                // Wake the loop so replay and SYNCED_TO_ODOO poll run without waiting for the next tick.
                // Guard: do not queue multiple wakeups (bounded at 1).
                try
                {
                    if (_wakeSignal.CurrentCount == 0)
                        _wakeSignal.Release();
                }
                catch (ObjectDisposedException) { /* shutdown race — safe to ignore */ }
                break;
        }
    }

    // ── Startup version check ──────────────────────────────────────────────

    /// <summary>
    /// Per requirements §15.13: call /agent/version-check on startup.
    /// If agent version is below minimum supported, disable FCC communication.
    /// Fail-open: if the check cannot be completed, FCC communication remains enabled.
    /// </summary>
    private async Task PerformStartupVersionCheckAsync(CancellationToken ct)
    {
        if (_versionChecker is null)
            return;

        try
        {
            var result = await _versionChecker.CheckVersionAsync(ct);
            if (result is null)
            {
                _logger.LogDebug("Version check returned no result — allowing FCC (fail-open)");
                return;
            }

            if (!result.Compatible)
            {
                _versionCompatible = false;
                _logger.LogCritical(
                    "VERSION INCOMPATIBLE: agent version is below minimum {MinVersion}. " +
                    "FCC communication DISABLED to prevent data format mismatches. " +
                    "Update agent to at least {MinVersion}.{UpdateUrl}",
                    result.MinimumVersion,
                    result.MinimumVersion,
                    result.UpdateUrl is not null ? $" Download: {result.UpdateUrl}" : "");
            }
            else
            {
                _versionCompatible = true;
                if (result.UpdateAvailable)
                {
                    _logger.LogInformation(
                        "Version check passed (compatible). Update available: {LatestVersion}",
                        result.LatestVersion);
                }
                else
                {
                    _logger.LogInformation("Version check passed (compatible, up to date)");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Version check failed on startup — allowing FCC (fail-open)");
        }
    }

    // ── Tick sub-interval helpers ─────────────────────────────────────────────

    private bool ShouldPollFcc(AgentConfiguration config)
        => config.IngestionMode switch
        {
            IngestionMode.Relay or IngestionMode.BufferAlways => true,
            IngestionMode.CloudDirect => IsCloudDirectPollTick(config),
            _ => false
        };

    /// <summary>
    /// CloudDirect safety-net polling runs every N ticks to approximate a 5-minute interval.
    /// Uses tick counting rather than an independent timer (architecture rule #10).
    /// </summary>
    private bool IsCloudDirectPollTick(AgentConfiguration config)
    {
        var cadenceSeconds = Math.Max(1, config.CloudSyncIntervalSeconds);
        const int CloudDirectIntervalSeconds = 300; // 5-minute safety-net interval
        var every = Math.Max(1, CloudDirectIntervalSeconds / cadenceSeconds);
        return _tick % every == 0;
    }

    /// <summary>
    /// H-07: SYNCED_TO_ODOO status poll runs every N ticks based on StatusPollIntervalSeconds / cadenceInterval.
    /// Previously used CloudSyncIntervalSeconds for both numerator and denominator, so every=1 always.
    /// Coalesced with the cloud-capable cycle (architecture rule #10).
    /// </summary>
    private bool IsSyncedToOdooPollTick(AgentConfiguration config)
    {
        var cadenceSeconds = Math.Max(1, config.CloudSyncIntervalSeconds);
        var statusPollInterval = Math.Max(30, config.StatusPollIntervalSeconds);
        var every = Math.Max(1, statusPollInterval / cadenceSeconds);
        return _tick % every == 0;
    }

    /// <summary>
    /// Config poll runs every N ticks based on configPollIntervalSeconds / cadenceInterval.
    /// Coalesced with cloud-capable cycle (architecture rule #10).
    /// </summary>
    private bool IsConfigPollTick(AgentConfiguration config)
    {
        var cadenceSeconds = Math.Max(1, config.CloudSyncIntervalSeconds);
        var configPollInterval = Math.Max(30, config.ConfigPollIntervalSeconds);
        var every = Math.Max(1, configPollInterval / cadenceSeconds);
        return _tick % every == 0;
    }

    private bool IsTelemetryTick(AgentConfiguration config)
    {
        var tickIntervalSeconds = Math.Max(1, config.CloudSyncIntervalSeconds);
        var every = Math.Max(1, config.TelemetryIntervalSeconds / tickIntervalSeconds);
        return _tick % every == 0;
    }
}

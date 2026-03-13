using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Connectivity;

/// <summary>
/// Dual-probe connectivity state machine.
/// Runs two independent probes (internet + FCC LAN) in parallel on a configurable interval.
///
/// DOWN detection: 3 consecutive failures required before transitioning to DOWN.
/// UP recovery: 1 success immediately transitions back to UP (fast recovery).
/// Initial state: <see cref="ConnectivityState.FullyOffline"/> — assumes worst until probes complete.
///
/// Architecture rule #10: this service drives the connectivity state that the CadenceController
/// reads. It does NOT manage recurring upload/poll/telemetry — that is the CadenceController's job.
/// </summary>
public sealed class ConnectivityManager : IConnectivityMonitor, IHostedService
{
    /// <summary>Number of consecutive probe failures required to transition a probe to DOWN.</summary>
    internal const int DownThreshold = 3;

    private readonly Func<CancellationToken, Task<bool>> _internetProbe;
    private readonly Func<CancellationToken, Task<bool>> _fccProbe;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly ILogger<ConnectivityManager> _logger;

    // Failure counters — only mutated from the single probe-loop Task. No concurrent writes.
    private int _internetFailures;
    private int _fccFailures;

    // Probe UP/DOWN state — written by probe loop, gated by DownThreshold.
    private bool _internetUp;
    private bool _fccUp;

    // Last successful FCC probe timestamp — used by telemetry reporter for heartbeat age.
    private DateTimeOffset? _lastFccSuccessAt;

    // Published snapshot — volatile reference; reference writes are atomic on .NET.
    private volatile ConnectivitySnapshot _current;

    private CancellationTokenSource? _cts;
    private Task? _probeTask;

    // ── Public surface (IConnectivityMonitor) ────────────────────────────────

    /// <inheritdoc/>
    public ConnectivitySnapshot Current => _current;

    /// <inheritdoc/>
    public DateTimeOffset? LastFccSuccessAtUtc => _lastFccSuccessAt;

    /// <inheritdoc/>
    public int FccConsecutiveFailures => _fccFailures;

    /// <inheritdoc/>
    public event EventHandler<ConnectivitySnapshot>? StateChanged;

    // ── DI constructor ────────────────────────────────────────────────────────

    /// <summary>
    /// Production constructor wired up by the DI container.
    /// The FCC adapter is resolved optionally — connectivity manager starts before
    /// the FCC adapter is fully configured.
    /// </summary>
    /// <remarks>
    /// H-10: Uses IOptionsMonitor so CloudBaseUrl is resolved at probe time (not cached at construction).
    /// </remarks>
    public ConnectivityManager(
        IHttpClientFactory httpFactory,
        IServiceProvider services,
        IOptionsMonitor<AgentConfiguration> config,
        ILogger<ConnectivityManager> logger)
        : this(
            ct =>
            {
                var cloudBaseUrl = config.CurrentValue.CloudBaseUrl;
                return PingCloudAsync(httpFactory, cloudBaseUrl, ct);
            },
            ct => ProbeFccAsync(services, ct),
            config,
            logger)
    {
    }

    /// <summary>
    /// BUG-014 fix: IFccAdapter is not registered in DI — only IFccAdapterFactory is.
    /// Resolve the factory and create an adapter on demand using current config.
    /// Returns false if config is incomplete (pre-registration) or adapter creation fails.
    /// </summary>
    private static async Task<bool> ProbeFccAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var factory = (IFccAdapterFactory?)services.GetService(typeof(IFccAdapterFactory));
            if (factory is null) return false;

            var monitor = (IOptionsMonitor<AgentConfiguration>?)services.GetService(
                typeof(IOptionsMonitor<AgentConfiguration>));
            var agentConfig = monitor?.CurrentValue;
            if (agentConfig is null || string.IsNullOrWhiteSpace(agentConfig.FccBaseUrl))
                return false;

            var resolved = DesktopFccRuntimeConfiguration.Resolve(
                agentConfig, siteConfig: null, TimeSpan.FromSeconds(5));
            var adapter = factory.Create(resolved.Vendor, resolved.ConnectionConfig);
            return await adapter.HeartbeatAsync(ct);
        }
        catch
        {
            // Config not ready, adapter creation failed, or heartbeat failed — FCC unreachable.
            return false;
        }
    }

    // ── Internal test constructor ─────────────────────────────────────────────

    /// <summary>
    /// Constructor for unit tests. Injects probe delegates directly so tests can control results.
    /// </summary>
    internal ConnectivityManager(
        Func<CancellationToken, Task<bool>> internetProbe,
        Func<CancellationToken, Task<bool>> fccProbe,
        IOptionsMonitor<AgentConfiguration> config,
        ILogger<ConnectivityManager> logger)
    {
        _internetProbe = internetProbe;
        _fccProbe = fccProbe;
        _config = config;
        _logger = logger;

        // Spec §5.4: "Initialize in FullyOffline on app start — assume worst until probes complete."
        _current = new ConnectivitySnapshot(ConnectivityState.FullyOffline, false, false, DateTimeOffset.UtcNow);
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Run probe loop on a thread-pool thread; do not block StartAsync.
        _probeTask = Task.Run(() => RunProbeLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null) return;

        await _cts.CancelAsync();

        if (_probeTask is not null)
        {
            try
            {
                await _probeTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ConnectivityManager probe loop did not exit cleanly");
            }
        }

        _cts.Dispose();
    }

    // ── Internal probe entry-point (also called directly from unit tests) ─────

    /// <summary>
    /// Runs both probes in parallel, updates failure counters, and publishes the derived state.
    /// Exposed as <c>internal</c> so unit tests can drive the state machine without a running host.
    /// </summary>
    internal async Task RunSingleCycleAsync(CancellationToken ct)
    {
        var internetTask = RunProbeWithTimeoutAsync(_internetProbe, ct);
        var fccTask = RunProbeWithTimeoutAsync(_fccProbe, ct);

        bool[] results = await Task.WhenAll(internetTask, fccTask);

        UpdateProbeState(ref _internetFailures, ref _internetUp, results[0], "Internet");
        UpdateProbeState(ref _fccFailures, ref _fccUp, results[1], "FCC");

        PublishState();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task RunProbeLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "ConnectivityManager probe loop started (downThreshold={Threshold} consecutive failures)",
            DownThreshold);

        // Run probes immediately on startup per spec §5.4.
        await RunSingleCycleAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            // Re-read interval on each iteration so hot-reloaded config changes take effect.
            var config = _config.CurrentValue;
            var interval = TimeSpan.FromSeconds(
                config.ConnectivityProbeIntervalSeconds > 0 ? config.ConnectivityProbeIntervalSeconds : 30);

            // ±20% jitter prevents synchronized probe bursts across multiple deployed devices.
            var jitterMs = (int)(interval.TotalMilliseconds * 0.2);
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(-jitterMs, jitterMs));

            try
            {
                await Task.Delay(interval + jitter, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ct.IsCancellationRequested) break;

            await RunSingleCycleAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("ConnectivityManager probe loop stopped");
    }

    /// <summary>
    /// Executes a single probe delegate with a hard 5-second deadline regardless of the
    /// caller's cancellation token. Returns false on timeout or any exception.
    /// </summary>
    private static async Task<bool> RunProbeWithTimeoutAsync(
        Func<CancellationToken, Task<bool>> probe,
        CancellationToken ct)
    {
        // Hard 5-second probe timeout per spec §5.4.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            return await probe(probeCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Probe-specific 5s timeout — FCC or cloud did not respond in time.
            return false;
        }
        catch (Exception)
        {
            // Any transport or protocol error → probe failed.
            return false;
        }
    }

    /// <summary>
    /// Updates a probe's failure counter and UP/DOWN flag according to the DOWN threshold rules.
    /// </summary>
    private void UpdateProbeState(ref int failures, ref bool isUp, bool success, string probeName)
    {
        if (success)
        {
            if (failures > 0)
            {
                _logger.LogDebug("{Probe} probe recovered after {Failures} consecutive failure(s)",
                    probeName, failures);
            }

            failures = 0;
            isUp = true; // Spec §5.4: "1 success immediately transitions back to UP"

            // Track last successful FCC heartbeat for telemetry reporting.
            if (probeName == "FCC")
                _lastFccSuccessAt = DateTimeOffset.UtcNow;
        }
        else
        {
            failures++;
            _logger.LogDebug("{Probe} probe failed ({Failures}/{Threshold})",
                probeName, failures, DownThreshold);

            // Spec §5.4: "3 consecutive failures required before transitioning to DOWN"
            if (failures >= DownThreshold)
                isUp = false;
        }
    }

    /// <summary>
    /// Derives the connectivity state from the current probe flags and publishes it.
    /// Fires <see cref="StateChanged"/> only when the state actually changes.
    /// </summary>
    private void PublishState()
    {
        var newState = (_internetUp, _fccUp) switch
        {
            (true, true)   => ConnectivityState.FullyOnline,
            (false, true)  => ConnectivityState.InternetDown,
            (true, false)  => ConnectivityState.FccUnreachable,
            (false, false) => ConnectivityState.FullyOffline,
        };

        var snapshot = new ConnectivitySnapshot(newState, _internetUp, _fccUp, DateTimeOffset.UtcNow);
        var previous = _current;
        _current = snapshot; // volatile write — visible to all readers immediately

        if (previous.State == newState) return;

        LogTransitionAudit(previous.State, newState);
        StateChanged?.Invoke(this, snapshot);
    }

    private void LogTransitionAudit(ConnectivityState from, ConnectivityState to)
    {
        // Always log the state change with full context for audit trail.
        _logger.LogInformation(
            "[CONNECTIVITY] {From} → {To} | internet={InternetUp} fcc={FccUp}",
            from, to, _internetUp, _fccUp);

        // Spec §5.4 side-effect descriptions per transition target.
        switch (to)
        {
            case ConnectivityState.InternetDown:
                _logger.LogWarning(
                    "[CONNECTIVITY] Internet DOWN — cloud upload suspended; FCC polling continues over LAN");
                break;
            case ConnectivityState.FccUnreachable:
                _logger.LogWarning(
                    "[CONNECTIVITY] FCC LAN UNREACHABLE — FCC polling suspended; ALERT Site Supervisor");
                break;
            case ConnectivityState.FullyOffline:
                _logger.LogWarning(
                    "[CONNECTIVITY] FULLY OFFLINE — all cloud and FCC workers suspended; local API continues");
                break;
            case ConnectivityState.FullyOnline:
                _logger.LogInformation(
                    "[CONNECTIVITY] Fully online — triggering immediate buffer replay and SYNCED_TO_ODOO sync");
                break;
        }
    }

    private static async Task<bool> PingCloudAsync(
        IHttpClientFactory httpFactory,
        string cloudBaseUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cloudBaseUrl)) return false;

        try
        {
            var http = httpFactory.CreateClient("cloud");
            var uri = cloudBaseUrl.TrimEnd('/') + "/health";
            using var response = await http.GetAsync(uri, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Provides live pump status with single-flight protection and stale cache fallback.
/// Architecture rule #13: SemaphoreSlim single-flight + last-known stale fallback when FCC is slow.
/// Live target: &lt;= 1 s; stale fallback: &lt;= 50 ms.
/// </summary>
public interface IPumpStatusService
{
    /// <summary>
    /// Returns live pump statuses from the FCC, falling back to stale cache
    /// when the FCC is unreachable or slow.
    /// </summary>
    Task<PumpStatusResult> GetPumpStatusAsync(int? pumpNumber, CancellationToken ct);
}

/// <summary>Result of a pump status query.</summary>
public sealed record PumpStatusResult(
    IReadOnlyList<PumpStatus> Pumps,
    string Source,
    DateTimeOffset? CachedAtUtc,
    DateTimeOffset ObservedAtUtc,
    PumpStatusCapability? Capability = null);

/// <inheritdoc />
public sealed class PumpStatusService : IPumpStatusService
{
    private static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(1);

    private readonly IFccAdapterFactory _adapterFactory;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly IConnectivityMonitor _connectivity;
    private readonly IConfigManager? _configManager;
    private readonly ILogger<PumpStatusService> _logger;

    // Architecture rule #13: single-flight protection — only one concurrent FCC call.
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private IReadOnlyList<PumpStatus>? _cachedStatuses;
    private DateTimeOffset _cachedAt;

    public PumpStatusService(
        IFccAdapterFactory adapterFactory,
        IOptionsMonitor<AgentConfiguration> config,
        IConnectivityMonitor connectivity,
        ILogger<PumpStatusService> logger,
        IConfigManager? configManager = null)
    {
        _adapterFactory = adapterFactory;
        _config = config;
        _connectivity = connectivity;
        _logger = logger;
        _configManager = configManager;
    }

    public async Task<PumpStatusResult> GetPumpStatusAsync(int? pumpNumber, CancellationToken ct)
    {
        var snapshot = _connectivity.Current;

        if (!snapshot.IsFccUp)
            return ServeStaleOrEmpty(pumpNumber);

        // Try to acquire single-flight lock (non-blocking).
        // If another request is in-flight, serve stale immediately.
        if (!await _singleFlight.WaitAsync(0, ct))
            return ServeStaleOrEmpty(pumpNumber);

        try
        {
            var config = _config.CurrentValue;
            var resolvedConfig = DesktopFccRuntimeConfiguration.Resolve(
                config, _configManager?.CurrentSiteConfig, LiveTimeout);
            var adapter = _adapterFactory.Create(resolvedConfig.Vendor, resolvedConfig.ConnectionConfig);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(LiveTimeout);

            var statuses = await adapter.GetPumpStatusAsync(cts.Token);

            // Update stale cache on success
            _cachedStatuses = statuses;
            _cachedAt = DateTimeOffset.UtcNow;

            var filtered = Filter(statuses, pumpNumber);
            return new PumpStatusResult(filtered, "live", null, DateTimeOffset.UtcNow, adapter.PumpStatusCapability);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Live fetch timed out — serve stale
            _logger.LogDebug("FCC pump status timed out — serving stale cache");
            return ServeStaleOrEmpty(pumpNumber);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FCC pump status query failed — serving stale cache");
            return ServeStaleOrEmpty(pumpNumber);
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private PumpStatusResult ServeStaleOrEmpty(int? pumpNumber)
    {
        if (_cachedStatuses is null)
            return new PumpStatusResult([], "unavailable", null, DateTimeOffset.UtcNow);

        var filtered = Filter(_cachedStatuses, pumpNumber);
        return new PumpStatusResult(filtered, "stale", _cachedAt, DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<PumpStatus> Filter(IReadOnlyList<PumpStatus> statuses, int? pumpNumber)
    {
        if (!pumpNumber.HasValue)
            return statuses;

        return statuses.Where(s => s.PumpNumber == pumpNumber.Value).ToList();
    }
}

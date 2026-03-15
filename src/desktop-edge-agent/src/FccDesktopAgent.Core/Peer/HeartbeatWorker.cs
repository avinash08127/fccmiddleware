using FccDesktopAgent.Core.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Peer;

/// <summary>
/// Background service that sends heartbeats to all known peers at a configurable interval.
/// Only active when <see cref="AgentConfiguration.SiteHaEnabled"/> is true.
/// Runs on an independent timer (not CadenceController) because heartbeat timing is critical
/// for failure detection accuracy.
/// </summary>
public sealed class HeartbeatWorker : BackgroundService
{
    private readonly IPeerCoordinator _coordinator;
    private readonly LanPeerAnnouncer _lanPeerAnnouncer;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly ILogger<HeartbeatWorker> _logger;

    public HeartbeatWorker(
        IPeerCoordinator coordinator,
        LanPeerAnnouncer lanPeerAnnouncer,
        IOptionsMonitor<AgentConfiguration> config,
        ILogger<HeartbeatWorker> logger)
    {
        _coordinator = coordinator;
        _lanPeerAnnouncer = lanPeerAnnouncer;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay to let services finish initializing
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        _logger.LogInformation("HeartbeatWorker started (interval={Interval}s)",
            _config.CurrentValue.HeartbeatIntervalSeconds);

        // P2-12: One-time broadcast on startup so LAN peers discover us immediately
        var startupBroadcastDone = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _config.CurrentValue;

            if (!cfg.SiteHaEnabled)
            {
                // HA not enabled — wait and check again
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                startupBroadcastDone = false; // Reset so broadcast fires if HA is later enabled
                continue;
            }

            if (!startupBroadcastDone)
            {
                try { _lanPeerAnnouncer.Broadcast(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Startup LAN peer announcement failed"); }
                startupBroadcastDone = true;
            }

            try
            {
                await _coordinator.SendHeartbeatToAllPeersAsync(stoppingToken);
                await _coordinator.EvaluateSuspectsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat cycle failed");
            }

            var interval = TimeSpan.FromSeconds(cfg.HeartbeatIntervalSeconds);
            await Task.Delay(interval, stoppingToken);
        }

        _logger.LogInformation("HeartbeatWorker stopped");
    }
}

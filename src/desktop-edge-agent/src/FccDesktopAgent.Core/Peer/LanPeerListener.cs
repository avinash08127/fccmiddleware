using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Peer;

/// <summary>
/// P2-13: Listens for UDP peer announcements on the station LAN.
///
/// When a valid PEER_ANNOUNCE is received from a different agent at the same site,
/// this listener:
/// 1. Adds the peer to the local peer coordinator's cache (temporary until cloud confirms)
/// 2. Triggers an immediate config poll to get the authoritative peer directory
///
/// Announcements from self or from a different siteCode are silently ignored.
/// Only active when <see cref="AgentConfiguration.SiteHaEnabled"/> is <c>true</c>.
///
/// Runs as a <see cref="BackgroundService"/>. If the UDP port is already in use
/// or broadcast is blocked, the listener logs a warning and retries periodically.
/// </summary>
public sealed class LanPeerListener : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly IPeerCoordinator _peerCoordinator;
    private readonly IConfigManager _configManager;
    private readonly ILogger<LanPeerListener> _logger;

    public LanPeerListener(
        IOptionsMonitor<AgentConfiguration> config,
        IPeerCoordinator peerCoordinator,
        IConfigManager configManager,
        ILogger<LanPeerListener> logger)
    {
        _config = config;
        _peerCoordinator = peerCoordinator;
        _configManager = configManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay to let services finish initializing
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        _logger.LogInformation("LanPeerListener started on port {Port}", LanPeerAnnouncer.BroadcastPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _config.CurrentValue;

            if (!cfg.SiteHaEnabled)
            {
                // HA not enabled — wait and check again
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            try
            {
                await ListenLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Socket error (port in use, permission denied, etc.) — retry after delay
                _logger.LogWarning(ex, "LAN peer listener error — retrying in 30s");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("LanPeerListener stopped");
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        using var client = new UdpClient(LanPeerAnnouncer.BroadcastPort);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // Set receive timeout so we can periodically check cancellation and HA status
        client.Client.ReceiveTimeout = 5_000;

        while (!ct.IsCancellationRequested)
        {
            // Re-check HA enabled in case config changed
            if (!_config.CurrentValue.SiteHaEnabled)
            {
                _logger.LogInformation("HA disabled during listen — closing socket");
                return;
            }

            try
            {
                var result = await client.ReceiveAsync(ct);
                var data = Encoding.UTF8.GetString(result.Buffer);
                HandleDatagram(data);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                // Expected — loop back to check cancellation
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning("Socket error in LAN peer listener: {Error}", ex.SocketErrorCode);
                throw; // Propagate to outer catch for retry
            }
        }
    }

    private void HandleDatagram(string data)
    {
        LanPeerAnnouncer.PeerAnnouncement? announcement;
        try
        {
            announcement = JsonSerializer.Deserialize<LanPeerAnnouncer.PeerAnnouncement>(data, JsonOptions);
        }
        catch
        {
            return; // Malformed — silently ignore
        }

        if (announcement is null || announcement.Type != "PEER_ANNOUNCE") return;

        var cfg = _config.CurrentValue;

        // Ignore announcements from self
        if (string.Equals(announcement.AgentId, cfg.DeviceId, StringComparison.OrdinalIgnoreCase))
            return;

        // Ignore announcements from a different site
        if (!string.Equals(announcement.SiteCode, cfg.SiteId, StringComparison.OrdinalIgnoreCase))
            return;

        var baseUrl = $"http://{announcement.PeerApiHost}:{announcement.PeerApiPort}";

        // Check if this is a genuinely new or updated peer
        var peers = _peerCoordinator.GetPeerStates();
        var isNewOrUpdated = !peers.TryGetValue(announcement.AgentId, out var existing)
                             || existing.PeerApiBaseUrl != baseUrl;

        _logger.LogInformation(
            "Received peer announcement: agent={AgentId}, host={Host}:{Port}, new={IsNew}",
            announcement.AgentId, announcement.PeerApiHost, announcement.PeerApiPort, isNewOrUpdated);

        // Add/update peer in coordinator's directory (temporary, until cloud config confirms)
        _peerCoordinator.InitializeFromConfig(
        [
            new PeerDirectoryEntry
            {
                AgentId = announcement.AgentId,
                DeviceClass = "UNKNOWN",
                PeerApiBaseUrl = baseUrl,
                RoleCapability = "PRIMARY_ELIGIBLE",
            }
        ]);

        // Trigger immediate config poll to get authoritative peer directory from cloud
        if (isNewOrUpdated)
        {
            _configManager.OnPeerDirectoryStale?.Invoke();
        }
    }
}

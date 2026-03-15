using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FccDesktopAgent.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Peer;

/// <summary>
/// P2-12: Broadcasts a UDP peer announcement on the station LAN so that other agents
/// on the same site can discover this peer without waiting for cloud config polls.
///
/// This is Layer 3 of peer discovery — a best-effort LAN broadcast that complements
/// cloud-delivered peer directories (Layer 1) and X-Peer-Directory-Version hints (Layer 2).
///
/// Broadcast is sent:
/// - On app startup when <c>SiteHaEnabled = true</c>
/// - After registration completes
/// - When agent role changes (e.g., promoted to PRIMARY)
///
/// Failures are silently logged — LAN may not support broadcast (e.g., AP isolation).
/// </summary>
public sealed class LanPeerAnnouncer
{
    public const int BroadcastPort = 18586;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly IPeerCoordinator _peerCoordinator;
    private readonly ILogger<LanPeerAnnouncer> _logger;

    public LanPeerAnnouncer(
        IOptionsMonitor<AgentConfiguration> config,
        IPeerCoordinator peerCoordinator,
        ILogger<LanPeerAnnouncer> logger)
    {
        _config = config;
        _peerCoordinator = peerCoordinator;
        _logger = logger;
    }

    /// <summary>
    /// Broadcast a PEER_ANNOUNCE datagram to 255.255.255.255:<see cref="BroadcastPort"/>.
    /// No-op when HA is disabled or required identity fields are unavailable.
    /// </summary>
    public void Broadcast()
    {
        var cfg = _config.CurrentValue;
        if (!cfg.SiteHaEnabled) return;

        var agentId = cfg.DeviceId;
        if (string.IsNullOrWhiteSpace(agentId)) return;

        var siteCode = cfg.SiteId;
        var peerApiPort = cfg.PeerApiPort;

        // Resolve host from local network interfaces
        var peerApiHost = GetLocalIpAddress();
        if (string.IsNullOrWhiteSpace(peerApiHost)) return;

        var announcement = new PeerAnnouncement
        {
            Type = "PEER_ANNOUNCE",
            AgentId = agentId,
            SiteCode = siteCode ?? string.Empty,
            PeerApiHost = peerApiHost,
            PeerApiPort = peerApiPort,
            PeerDirectoryVersion = _peerCoordinator.LocalEpoch,
        };

        try
        {
            var json = JsonSerializer.Serialize(announcement, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            using var client = new UdpClient { EnableBroadcast = true };
            client.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, BroadcastPort));

            _logger.LogInformation(
                "Broadcast peer announcement: agent={AgentId}, site={SiteCode}, host={Host}:{Port}",
                agentId, siteCode, peerApiHost, peerApiPort);
        }
        catch (Exception ex)
        {
            // Best-effort: LAN may not support broadcast (AP isolation, firewall, etc.)
            _logger.LogWarning(ex, "Failed to broadcast peer announcement");
        }
    }

    private static string? GetLocalIpAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                              && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .FirstOrDefault(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    public sealed class PeerAnnouncement
    {
        public string Type { get; set; } = "PEER_ANNOUNCE";
        public string AgentId { get; set; } = string.Empty;
        public string SiteCode { get; set; } = string.Empty;
        public string PeerApiHost { get; set; } = string.Empty;
        public int PeerApiPort { get; set; }
        public long PeerDirectoryVersion { get; set; }
    }
}

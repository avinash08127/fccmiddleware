using System.Collections.Concurrent;
using FccDesktopAgent.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Peer;

/// <summary>
/// Manages peer directory, heartbeat exchange, and leadership claim evaluation.
/// </summary>
public interface IPeerCoordinator
{
    /// <summary>Build a health response from the local agent's current state.</summary>
    PeerHealthResponse BuildHealthResponse();

    /// <summary>Process an incoming heartbeat from a peer.</summary>
    PeerHeartbeatResponse HandleIncomingHeartbeat(PeerHeartbeatRequest request);

    /// <summary>Evaluate an incoming leadership claim.</summary>
    PeerLeadershipClaimResponse HandleLeadershipClaim(PeerLeadershipClaimRequest request);

    /// <summary>Send heartbeats to all known peers.</summary>
    Task SendHeartbeatToAllPeersAsync(CancellationToken ct);

    /// <summary>Returns a snapshot of the current peer directory.</summary>
    IReadOnlyDictionary<string, PeerState> GetPeerStates();

    /// <summary>Whether the current PRIMARY peer is suspected down.</summary>
    bool IsPrimarySuspected { get; }

    /// <summary>Direct health probe to a specific peer agent.</summary>
    Task<PeerHealthResponse?> DirectHealthProbeAsync(string agentId, CancellationToken ct);

    /// <summary>Evaluate all peers for suspect/confirmed-down status based on missed heartbeats.</summary>
    Task EvaluateSuspectsAsync(CancellationToken ct);

    /// <summary>Bootstrap the peer directory from cloud-provided peer entries, filtering out self.</summary>
    void InitializeFromConfig(IEnumerable<PeerDirectoryEntry> peerEntries);

    /// <summary>The local agent's current leader epoch.</summary>
    long LocalEpoch { get; set; }
}

/// <summary>
/// Entry from cloud config representing a peer in the site HA cluster.
/// </summary>
public sealed class PeerDirectoryEntry
{
    public string AgentId { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = string.Empty;
    public string PeerApiBaseUrl { get; set; } = string.Empty;
    public string RoleCapability { get; set; } = string.Empty;
}

public sealed class PeerCoordinator : IPeerCoordinator
{
    private readonly IPeerHttpClient _peerClient;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly ILogger<PeerCoordinator> _logger;
    private readonly ConcurrentDictionary<string, PeerState> _peers = new();
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    private static readonly string AgentVersion =
        typeof(PeerCoordinator).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public long LocalEpoch { get; set; }

    public PeerCoordinator(
        IPeerHttpClient peerClient,
        IOptionsMonitor<AgentConfiguration> config,
        ILogger<PeerCoordinator> logger)
    {
        _peerClient = peerClient;
        _config = config;
        _logger = logger;
    }

    public PeerHealthResponse BuildHealthResponse()
    {
        var cfg = _config.CurrentValue;
        return new PeerHealthResponse
        {
            AgentId = cfg.DeviceId,
            SiteCode = cfg.SiteId,
            CurrentRole = cfg.CurrentRole,
            LeaderEpoch = LocalEpoch,
            FccReachable = true, // TODO: wire to connectivity monitor
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAtUtc).TotalSeconds,
            AppVersion = AgentVersion,
            HighWaterMarkSeq = 0, // TODO: wire to replication sequence
            ReportedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public PeerHeartbeatResponse HandleIncomingHeartbeat(PeerHeartbeatRequest request)
    {
        var cfg = _config.CurrentValue;

        _peers.AddOrUpdate(
            request.AgentId,
            _ => new PeerState
            {
                AgentId = request.AgentId,
                DeviceClass = request.DeviceClass,
                CurrentRole = request.CurrentRole,
                LeaderEpoch = request.LeaderEpoch,
                PeerApiBaseUrl = string.Empty, // Will be populated from config
                LastHeartbeatReceivedUtc = DateTimeOffset.UtcNow,
                ConsecutiveMissedHeartbeats = 0,
                SuspectStatus = SuspectStatus.Healthy,
                ReplicationLagSeconds = request.ReplicationLagSeconds,
            },
            (_, existing) =>
            {
                existing.CurrentRole = request.CurrentRole;
                existing.LeaderEpoch = request.LeaderEpoch;
                existing.DeviceClass = request.DeviceClass;
                existing.LastHeartbeatReceivedUtc = DateTimeOffset.UtcNow;
                existing.ConsecutiveMissedHeartbeats = 0;
                existing.SuspectStatus = SuspectStatus.Healthy;
                existing.ReplicationLagSeconds = request.ReplicationLagSeconds;
                return existing;
            });

        _logger.LogDebug("Heartbeat received from {AgentId} role={Role} epoch={Epoch}",
            request.AgentId, request.CurrentRole, request.LeaderEpoch);

        return new PeerHeartbeatResponse
        {
            AgentId = cfg.DeviceId,
            CurrentRole = cfg.CurrentRole,
            LeaderEpoch = LocalEpoch,
            Accepted = true,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public PeerLeadershipClaimResponse HandleLeadershipClaim(PeerLeadershipClaimRequest request)
    {
        // Accept if proposed epoch is higher than our local epoch
        if (request.ProposedEpoch > LocalEpoch)
        {
            _logger.LogInformation(
                "Accepting leadership claim from {CandidateId} epoch={ProposedEpoch} (local={LocalEpoch})",
                request.CandidateAgentId, request.ProposedEpoch, LocalEpoch);

            LocalEpoch = request.ProposedEpoch;

            return new PeerLeadershipClaimResponse
            {
                Accepted = true,
                CurrentEpoch = LocalEpoch,
            };
        }

        _logger.LogInformation(
            "Rejecting leadership claim from {CandidateId} epoch={ProposedEpoch} (local={LocalEpoch})",
            request.CandidateAgentId, request.ProposedEpoch, LocalEpoch);

        return new PeerLeadershipClaimResponse
        {
            Accepted = false,
            Reason = $"Local epoch {LocalEpoch} >= proposed epoch {request.ProposedEpoch}",
            CurrentEpoch = LocalEpoch,
        };
    }

    public async Task SendHeartbeatToAllPeersAsync(CancellationToken ct)
    {
        var cfg = _config.CurrentValue;
        var heartbeat = new PeerHeartbeatRequest
        {
            AgentId = cfg.DeviceId,
            SiteCode = cfg.SiteId,
            CurrentRole = cfg.CurrentRole,
            LeaderEpoch = LocalEpoch,
            DeviceClass = "desktop",
            AppVersion = AgentVersion,
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAtUtc).TotalSeconds,
            SentAtUtc = DateTimeOffset.UtcNow,
        };

        foreach (var (agentId, peer) in _peers)
        {
            if (string.IsNullOrEmpty(peer.PeerApiBaseUrl))
                continue;

            try
            {
                var response = await _peerClient.SendHeartbeatAsync(peer.PeerApiBaseUrl, heartbeat, ct);
                if (response is not null)
                {
                    peer.LastHeartbeatReceivedUtc = DateTimeOffset.UtcNow;
                    peer.ConsecutiveMissedHeartbeats = 0;
                    peer.CurrentRole = response.CurrentRole;
                    peer.LeaderEpoch = response.LeaderEpoch;
                    peer.SuspectStatus = SuspectStatus.Healthy;
                }
                else
                {
                    peer.ConsecutiveMissedHeartbeats++;
                    _logger.LogWarning("Heartbeat to {AgentId} returned null (missed={Missed})",
                        agentId, peer.ConsecutiveMissedHeartbeats);
                }
            }
            catch (Exception ex)
            {
                peer.ConsecutiveMissedHeartbeats++;
                _logger.LogWarning(ex, "Failed to send heartbeat to {AgentId} at {Url} (missed={Missed})",
                    agentId, peer.PeerApiBaseUrl, peer.ConsecutiveMissedHeartbeats);
            }
        }
    }

    public IReadOnlyDictionary<string, PeerState> GetPeerStates() =>
        new Dictionary<string, PeerState>(_peers);

    public bool IsPrimarySuspected
    {
        get
        {
            foreach (var (_, peer) in _peers)
            {
                if (peer.CurrentRole == "PRIMARY" &&
                    peer.SuspectStatus is SuspectStatus.Suspected or SuspectStatus.ConfirmedDown)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public async Task<PeerHealthResponse?> DirectHealthProbeAsync(string agentId, CancellationToken ct)
    {
        if (!_peers.TryGetValue(agentId, out var peer) || string.IsNullOrEmpty(peer.PeerApiBaseUrl))
        {
            _logger.LogWarning("Cannot probe unknown peer {AgentId}", agentId);
            return null;
        }

        try
        {
            return await _peerClient.GetHealthAsync(peer.PeerApiBaseUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Direct health probe to {AgentId} failed", agentId);
            return null;
        }
    }

    public async Task EvaluateSuspectsAsync(CancellationToken ct)
    {
        var cfg = _config.CurrentValue;
        var heartbeatInterval = TimeSpan.FromSeconds(cfg.HeartbeatIntervalSeconds);
        // Number of missed heartbeats before suspicion (failoverTimeout / heartbeatInterval)
        var missedThreshold = cfg.FailoverTimeoutSeconds / cfg.HeartbeatIntervalSeconds;

        foreach (var (agentId, peer) in _peers)
        {
            if (peer.CurrentRole != "PRIMARY")
                continue;

            var elapsed = DateTimeOffset.UtcNow - peer.LastHeartbeatReceivedUtc;
            var missedCount = (int)(elapsed / heartbeatInterval);
            peer.ConsecutiveMissedHeartbeats = Math.Max(peer.ConsecutiveMissedHeartbeats, missedCount);

            if (peer.ConsecutiveMissedHeartbeats < missedThreshold)
            {
                if (peer.SuspectStatus != SuspectStatus.Healthy)
                {
                    _logger.LogInformation("Peer {AgentId} suspicion cleared", agentId);
                    peer.SuspectStatus = SuspectStatus.Healthy;
                }
                continue;
            }

            if (peer.SuspectStatus == SuspectStatus.Healthy)
            {
                peer.SuspectStatus = SuspectStatus.Suspected;
                _logger.LogWarning("Peer {AgentId} suspected down (missed={Missed}, threshold={Threshold})",
                    agentId, peer.ConsecutiveMissedHeartbeats, missedThreshold);
            }

            if (peer.SuspectStatus == SuspectStatus.Suspected)
            {
                // Perform a direct health probe to confirm
                var health = await DirectHealthProbeAsync(agentId, ct);
                if (health is not null)
                {
                    // Peer responded — clear suspicion
                    peer.SuspectStatus = SuspectStatus.Healthy;
                    peer.ConsecutiveMissedHeartbeats = 0;
                    peer.LastHeartbeatReceivedUtc = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Peer {AgentId} responded to health probe — suspicion cleared", agentId);
                }
                else
                {
                    // Probe failed — confirm down
                    peer.SuspectStatus = SuspectStatus.ConfirmedDown;
                    _logger.LogWarning("Peer {AgentId} confirmed down after failed health probe", agentId);
                }
            }
        }
    }

    public void InitializeFromConfig(IEnumerable<PeerDirectoryEntry> peerEntries)
    {
        var cfg = _config.CurrentValue;
        var selfId = cfg.DeviceId;

        foreach (var entry in peerEntries)
        {
            // Filter out self
            if (string.Equals(entry.AgentId, selfId, StringComparison.OrdinalIgnoreCase))
                continue;

            _peers.AddOrUpdate(
                entry.AgentId,
                _ => new PeerState
                {
                    AgentId = entry.AgentId,
                    DeviceClass = entry.DeviceClass,
                    PeerApiBaseUrl = entry.PeerApiBaseUrl,
                    CurrentRole = "UNKNOWN",
                    LastHeartbeatReceivedUtc = DateTimeOffset.MinValue,
                    SuspectStatus = SuspectStatus.Healthy,
                },
                (_, existing) =>
                {
                    existing.DeviceClass = entry.DeviceClass;
                    existing.PeerApiBaseUrl = entry.PeerApiBaseUrl;
                    return existing;
                });

            _logger.LogInformation("Peer registered: {AgentId} class={Class} url={Url}",
                entry.AgentId, entry.DeviceClass, entry.PeerApiBaseUrl);
        }
    }
}

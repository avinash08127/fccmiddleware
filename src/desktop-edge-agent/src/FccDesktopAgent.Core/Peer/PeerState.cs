namespace FccDesktopAgent.Core.Peer;

/// <summary>
/// Suspect status for a peer agent in the HA cluster.
/// </summary>
public enum SuspectStatus
{
    Healthy,
    Suspected,
    ConfirmedDown
}

/// <summary>
/// Runtime state of a known peer agent in the site HA cluster.
/// Maintained by <see cref="PeerCoordinator"/> via heartbeat exchange.
/// </summary>
public sealed class PeerState
{
    /// <summary>Unique agent identifier (UUID v4).</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Device class (e.g. "desktop", "android").</summary>
    public string DeviceClass { get; set; } = string.Empty;

    /// <summary>Current runtime role: PRIMARY, STANDBY_HOT, RECOVERING, READ_ONLY, OFFLINE.</summary>
    public string CurrentRole { get; set; } = string.Empty;

    /// <summary>Leader epoch observed from this peer's last heartbeat.</summary>
    public long LeaderEpoch { get; set; }

    /// <summary>Base URL for this peer's peer API (e.g. http://192.168.1.101:8586).</summary>
    public string PeerApiBaseUrl { get; set; } = string.Empty;

    /// <summary>When the last heartbeat was received from this peer (UTC).</summary>
    public DateTimeOffset LastHeartbeatReceivedUtc { get; set; }

    /// <summary>Number of consecutive heartbeat intervals missed by this peer.</summary>
    public int ConsecutiveMissedHeartbeats { get; set; }

    /// <summary>Current suspect status based on heartbeat monitoring.</summary>
    public SuspectStatus SuspectStatus { get; set; } = SuspectStatus.Healthy;

    /// <summary>Replication lag reported by this peer in its last heartbeat.</summary>
    public double ReplicationLagSeconds { get; set; }
}

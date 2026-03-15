namespace FccDesktopAgent.Core.Replication;

/// <summary>
/// Audit event type constants for the HA replication subsystem.
/// Used with <see cref="FccDesktopAgent.Core.Buffer.IAuditLogger"/> for structured event logging.
/// </summary>
public static class ReplicationAuditEvents
{
    /// <summary>A peer has missed enough heartbeats to be suspected down.</summary>
    public const string PEER_SUSPECTED = "PEER_SUSPECTED";

    /// <summary>A previously suspected peer has responded and suspicion was cleared.</summary>
    public const string PEER_SUSPICION_CLEARED = "PEER_SUSPICION_CLEARED";

    /// <summary>A suspected peer has been confirmed down after a failed direct health probe.</summary>
    public const string PEER_CONFIRMED_DOWN = "PEER_CONFIRMED_DOWN";

    /// <summary>A previously confirmed-down peer has come back online.</summary>
    public const string PEER_RECOVERED = "PEER_RECOVERED";

    /// <summary>This agent has started a leader election process.</summary>
    public const string ELECTION_STARTED = "ELECTION_STARTED";

    /// <summary>This agent has won a leader election and assumed PRIMARY role.</summary>
    public const string ELECTION_WON = "ELECTION_WON";

    /// <summary>This agent's leadership claim was rejected or a higher-priority peer was found.</summary>
    public const string ELECTION_LOST = "ELECTION_LOST";

    /// <summary>This agent demoted itself after observing a higher epoch from another agent.</summary>
    public const string SELF_DEMOTION = "SELF_DEMOTION";

    /// <summary>A full bootstrap snapshot has been applied from the primary.</summary>
    public const string REPLICATION_BOOTSTRAP_COMPLETE = "REPLICATION_BOOTSTRAP_COMPLETE";

    /// <summary>A delta sync batch has been applied from the primary.</summary>
    public const string REPLICATION_DELTA_APPLIED = "REPLICATION_DELTA_APPLIED";

    /// <summary>Startup recovery process has begun.</summary>
    public const string RECOVERY_STARTED = "RECOVERY_STARTED";

    /// <summary>Startup recovery process has completed and a role has been determined.</summary>
    public const string RECOVERY_COMPLETE = "RECOVERY_COMPLETE";

    /// <summary>An operator-initiated switchover has started.</summary>
    public const string SWITCHOVER_STARTED = "SWITCHOVER_STARTED";

    /// <summary>An operator-initiated switchover has completed successfully.</summary>
    public const string SWITCHOVER_COMPLETED = "SWITCHOVER_COMPLETED";

    /// <summary>An operator-initiated switchover has failed.</summary>
    public const string SWITCHOVER_FAILED = "SWITCHOVER_FAILED";

    // PascalCase aliases for backward compatibility with existing code
    public const string SwitchoverStarted = SWITCHOVER_STARTED;
    public const string SwitchoverCompleted = SWITCHOVER_COMPLETED;
    public const string SwitchoverFailed = SWITCHOVER_FAILED;
}

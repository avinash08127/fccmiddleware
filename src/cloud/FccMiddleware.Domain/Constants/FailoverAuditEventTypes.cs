namespace FccMiddleware.Domain.Constants;

/// <summary>
/// Audit event types for the multi-agent HA failover workstream.
/// Keep values stable — portal filters, analytics, and compliance queries depend on them.
/// </summary>
public static class FailoverAuditEventTypes
{
    // ── Leadership ──────────────────────────────────────────────────────
    public const string HaPrimaryElected = "HA_PRIMARY_ELECTED";
    public const string HaPrimarySelfDemoted = "HA_PRIMARY_SELF_DEMOTED";

    // ── Switchover ──────────────────────────────────────────────────────
    public const string HaSwitchoverStarted = "HA_SWITCHOVER_STARTED";
    public const string HaSwitchoverCompleted = "HA_SWITCHOVER_COMPLETED";
    public const string HaSwitchoverFailed = "HA_SWITCHOVER_FAILED";

    // ── Write Fencing ───────────────────────────────────────────────────
    public const string HaStaleWriterRejected = "HA_STALE_WRITER_REJECTED";

    // ── Promotion ───────────────────────────────────────────────────────
    public const string HaPromotionBlocked = "HA_PROMOTION_BLOCKED";

    // ── Recovery ────────────────────────────────────────────────────────
    public const string HaRecoveryStarted = "HA_RECOVERY_STARTED";
    public const string HaRecoveryCompleted = "HA_RECOVERY_COMPLETED";

    // ── Replication ─────────────────────────────────────────────────────
    public const string HaReplicationLagExceeded = "HA_REPLICATION_LAG_EXCEEDED";

    // ── Epoch ───────────────────────────────────────────────────────────
    public const string HaEpochIncremented = "HA_EPOCH_INCREMENTED";

    // ── Peer Health ─────────────────────────────────────────────────────
    public const string HaPeerSuspected = "HA_PEER_SUSPECTED";
    public const string HaPeerRecovered = "HA_PEER_RECOVERED";
}

namespace FccDesktopAgent.Core.Buffer.Entities;

/// <summary>
/// Audit trail for pump block/unblock actions.
/// Ported from legacy InsertBlockUnblockHistory(fpId, actionType, source, note).
/// </summary>
public sealed class PumpBlockHistory
{
    public int Id { get; set; }

    /// <summary>Fuelling point ID (canonical pump number).</summary>
    public int FpId { get; set; }

    /// <summary>Action taken: "Blocked" or "Unblock".</summary>
    public string ActionType { get; set; } = "";

    /// <summary>Source of the action: "Middleware", "Attendant", "Manager".</summary>
    public string Source { get; set; } = "";

    /// <summary>Human-readable note about why the action was taken.</summary>
    public string Note { get; set; } = "";

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public bool IsSynced { get; set; }
    public DateTimeOffset? SyncedAtUtc { get; set; }
}

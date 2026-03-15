namespace FccDesktopAgent.Core.Buffer.Entities;

/// <summary>
/// Tracks per-pump transaction limits.
/// Ported from legacy FpLimitDto: FpId, MaxLimit, CurrentCount, Status, IsAllowed.
/// </summary>
public sealed class PumpLimit
{
    public int Id { get; set; }

    /// <summary>Fuelling point ID (canonical pump number).</summary>
    public int FpId { get; set; }

    /// <summary>Maximum allowed transactions before the pump is blocked.</summary>
    public int MaxLimit { get; set; }

    /// <summary>Current transaction count in this session.</summary>
    public int CurrentCount { get; set; }

    /// <summary>Active status: "active", "blocked", "reset".</summary>
    public string Status { get; set; } = "active";

    /// <summary>Manual override: if false, pump should be blocked regardless of count.</summary>
    public bool IsAllowed { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

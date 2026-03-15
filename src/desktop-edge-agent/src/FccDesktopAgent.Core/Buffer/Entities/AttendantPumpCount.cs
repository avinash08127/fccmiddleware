namespace FccDesktopAgent.Core.Buffer.Entities;

/// <summary>
/// Tracks transaction counts per attendant per pump per session.
/// Ported from legacy AttendantPumpCountUpdate: SessionId, EmpTagNo, NewMaxTransaction, PumpNumber.
/// </summary>
public sealed class AttendantPumpCount
{
    public int Id { get; set; }
    public string SessionId { get; set; } = "";
    public string EmpTagNo { get; set; } = "";
    public int PumpNumber { get; set; }
    public int MaxTransactions { get; set; }
    public int CurrentCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

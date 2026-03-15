namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Audit trail for pump control actions (block/unblock/emergency stop).
/// </summary>
public class PumpControlHistoryRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = "";
    public int PumpNumber { get; set; }
    public string ActionType { get; set; } = "";  // "EmergencyStop", "CancelEmergencyStop", "Close", "Open"
    public string Source { get; set; } = "";       // "EdgeAgent", "Attendant", "CloudPortal"
    public string? Note { get; set; }
    public string DeviceId { get; set; } = "";
    public DateTimeOffset ActionAtUtc { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

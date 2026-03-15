namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Cumulative pump totals snapshot for shift reconciliation.
/// </summary>
public class PumpTotalsRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = "";
    public int PumpNumber { get; set; }
    public long TotalVolumeMicrolitres { get; set; }
    public long TotalAmountMinorUnits { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string DeviceId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

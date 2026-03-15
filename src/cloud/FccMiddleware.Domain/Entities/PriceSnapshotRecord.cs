namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Fuel price snapshot from an edge agent.
/// Records price changes for historical tracking.
/// </summary>
public class PriceSnapshotRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = "";
    public string PriceSetId { get; set; } = "";
    public string GradeId { get; set; } = "";
    public string GradeName { get; set; } = "";
    public long PriceMinorUnits { get; set; }
    public string CurrencyCode { get; set; } = "";
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string DeviceId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Optional override of operational thresholds for a specific legal entity.
/// </summary>
public class LegalEntitySettingsOverride
{
    public Guid Id { get; set; }
    public Guid LegalEntityId { get; set; }
    public decimal? AmountTolerancePercent { get; set; }
    public long? AmountToleranceAbsoluteMinorUnits { get; set; }
    public int? TimeWindowMinutes { get; set; }
    public int? StalePendingThresholdDays { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public LegalEntity LegalEntity { get; set; } = null!;
}

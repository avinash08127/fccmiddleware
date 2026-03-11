namespace FccMiddleware.Application.Reconciliation;

public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    public decimal DefaultAmountTolerancePercent { get; set; } = 2.00m;
    public long DefaultAmountToleranceAbsolute { get; set; } = 0;
    public int DefaultTimeWindowMinutes { get; set; } = 15;
}

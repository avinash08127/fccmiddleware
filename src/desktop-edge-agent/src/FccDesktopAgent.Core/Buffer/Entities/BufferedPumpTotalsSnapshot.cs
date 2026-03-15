namespace FccDesktopAgent.Core.Buffer.Entities;

/// <summary>
/// Pump totals snapshot buffered locally before upload to the cloud site-data endpoint.
/// </summary>
public sealed class BufferedPumpTotalsSnapshot
{
    public int Id { get; set; }
    public int PumpNumber { get; set; }
    public long TotalVolumeMicrolitres { get; set; }
    public long TotalAmountMinorUnits { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public DateTimeOffset ObservedAtUtc { get; set; }
    public bool IsSynced { get; set; }
    public DateTimeOffset? SyncedAtUtc { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

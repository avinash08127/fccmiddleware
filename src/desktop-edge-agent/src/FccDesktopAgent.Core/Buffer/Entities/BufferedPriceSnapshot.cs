namespace FccDesktopAgent.Core.Buffer.Entities;

/// <summary>
/// Price snapshot buffered locally before upload to the cloud site-data endpoint.
/// </summary>
public sealed class BufferedPriceSnapshot
{
    public int Id { get; set; }
    public string PriceSetId { get; set; } = "01";
    public string GradeId { get; set; } = string.Empty;
    public string GradeName { get; set; } = string.Empty;
    public long PriceMinorUnits { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public DateTimeOffset ObservedAtUtc { get; set; }
    public bool IsSynced { get; set; }
    public DateTimeOffset? SyncedAtUtc { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

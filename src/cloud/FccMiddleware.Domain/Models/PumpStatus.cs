using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Domain.Models;

/// <summary>
/// Real-time pump status snapshot as reported by the FCC or synthesized by the Edge Agent.
/// Matches pump-status.schema.json v1.0.
/// Numeric fields (volume, amount, price) are strings to preserve decimal precision.
/// </summary>
public class PumpStatus
{
    public string SchemaVersion { get; set; } = "1.0";
    public string SiteCode { get; set; } = null!;
    public int PumpNumber { get; set; }
    public int NozzleNumber { get; set; }
    public PumpState State { get; set; }
    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }

    /// <summary>Current dispensed volume in litres. String to preserve up to 3 decimal places.</summary>
    public string? CurrentVolumeLitres { get; set; }

    /// <summary>Current transaction amount in currency units. String to preserve up to 2 decimal places.</summary>
    public string? CurrentAmount { get; set; }

    /// <summary>Unit price per litre. String to preserve up to 4 decimal places.</summary>
    public string? UnitPrice { get; set; }

    public string CurrencyCode { get; set; } = null!;

    /// <summary>Vendor-specific raw status code from the FCC. Null if not provided.</summary>
    public string? FccStatusCode { get; set; }

    /// <summary>Monotonically increasing counter for ordering concurrent status messages.</summary>
    public int StatusSequence { get; set; }

    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset? LastChangedAtUtc { get; set; }
    public PumpStatusSource Source { get; set; }
}

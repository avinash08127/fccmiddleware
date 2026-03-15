using System.Text.Json.Serialization;

namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Pump nozzle status snapshot from the FCC or synthesized by the Edge Agent.
/// </summary>
public sealed record PumpStatus
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1.0";

    [JsonPropertyName("siteCode")]
    public string SiteCode { get; init; } = string.Empty;

    [JsonPropertyName("pumpNumber")]
    public int PumpNumber { get; init; }

    [JsonPropertyName("nozzleNumber")]
    public int NozzleNumber { get; init; }

    [JsonPropertyName("state")]
    public PumpState State { get; init; }

    [JsonPropertyName("currencyCode")]
    public string CurrencyCode { get; init; } = string.Empty;

    [JsonPropertyName("statusSequence")]
    public long StatusSequence { get; init; }

    [JsonPropertyName("observedAtUtc")]
    public DateTimeOffset ObservedAtUtc { get; init; }

    [JsonPropertyName("source")]
    public PumpStatusSource Source { get; init; }

    [JsonPropertyName("productCode")]
    public string? ProductCode { get; init; }

    [JsonPropertyName("productName")]
    public string? ProductName { get; init; }

    /// <summary>Decimal string representation of current volume in litres.</summary>
    [JsonPropertyName("currentVolumeLitres")]
    public string? CurrentVolumeLitres { get; init; }

    /// <summary>Decimal string representation of current dispensed amount.</summary>
    [JsonPropertyName("currentAmount")]
    public string? CurrentAmount { get; init; }

    /// <summary>Decimal string representation of unit price.</summary>
    [JsonPropertyName("unitPrice")]
    public string? UnitPrice { get; init; }

    [JsonPropertyName("fccStatusCode")]
    public string? FccStatusCode { get; init; }

    [JsonPropertyName("lastChangedAtUtc")]
    public DateTimeOffset? LastChangedAtUtc { get; init; }

    /// <summary>
    /// Extended supplemental status from FpStatus_3 (16 parameter IDs).
    /// Null when the FCC does not include supplemental data in the response.
    /// </summary>
    [JsonPropertyName("supplemental")]
    public PumpStatusSupplemental? Supplemental { get; init; }
}

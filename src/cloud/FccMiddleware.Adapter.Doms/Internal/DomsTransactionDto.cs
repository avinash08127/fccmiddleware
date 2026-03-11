using System.Text.Json.Serialization;

namespace FccMiddleware.Adapter.Doms.Internal;

/// <summary>
/// Deserialisation target for a single DOMS transaction JSON object.
///
/// DOMS MVP field conventions (§5.5 of adapter interface contracts):
/// - Monetary amounts are in minor currency units (e.g., cents, ngwe, kobo).
/// - Volume is in microlitres.
/// - Times are ISO 8601 UTC strings.
/// </summary>
internal sealed class DomsTransactionDto
{
    [JsonPropertyName("transactionId")]
    public string TransactionId { get; init; } = null!;

    [JsonPropertyName("pumpNumber")]
    public int PumpNumber { get; init; }

    [JsonPropertyName("nozzleNumber")]
    public int NozzleNumber { get; init; }

    /// <summary>Raw DOMS product code — mapped to canonical code via SiteFccConfig.ProductCodeMapping.</summary>
    [JsonPropertyName("productCode")]
    public string ProductCode { get; init; } = null!;

    /// <summary>Dispensed volume in microlitres.</summary>
    [JsonPropertyName("volumeMicrolitres")]
    public long VolumeMicrolitres { get; init; }

    /// <summary>Total transaction amount in minor currency units.</summary>
    [JsonPropertyName("amountMinorUnits")]
    public long AmountMinorUnits { get; init; }

    /// <summary>Price per litre in minor currency units.</summary>
    [JsonPropertyName("unitPriceMinorPerLitre")]
    public long UnitPriceMinorPerLitre { get; init; }

    [JsonPropertyName("startTime")]
    public DateTimeOffset StartTime { get; init; }

    [JsonPropertyName("endTime")]
    public DateTimeOffset EndTime { get; init; }

    [JsonPropertyName("fccCorrelationId")]
    public string? FccCorrelationId { get; init; }

    [JsonPropertyName("odooOrderId")]
    public string? OdooOrderId { get; init; }

    [JsonPropertyName("attendantId")]
    public string? AttendantId { get; init; }

    [JsonPropertyName("receiptNumber")]
    public string? ReceiptNumber { get; init; }
}

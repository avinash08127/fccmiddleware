using System.Text.Json.Serialization;

namespace FccMiddleware.Adapter.Petronite.Internal;

/// <summary>
/// Deserialisation target for a Petronite webhook transaction payload.
/// </summary>
internal sealed class PetroniteWebhookPayload
{
    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = null!;

    [JsonPropertyName("transaction")]
    public PetroniteTransactionDto? Transaction { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }
}

/// <summary>
/// Transaction data within a Petronite webhook payload.
/// </summary>
internal sealed class PetroniteTransactionDto
{
    [JsonPropertyName("orderId")]
    public string OrderId { get; init; } = null!;

    [JsonPropertyName("nozzleId")]
    public string NozzleId { get; init; } = null!;

    [JsonPropertyName("pumpNumber")]
    public int PumpNumber { get; init; }

    [JsonPropertyName("nozzleNumber")]
    public int NozzleNumber { get; init; }

    [JsonPropertyName("productCode")]
    public string ProductCode { get; init; } = null!;

    /// <summary>Volume in litres as decimal.</summary>
    [JsonPropertyName("volumeLitres")]
    public decimal VolumeLitres { get; init; }

    /// <summary>Amount in major currency units as decimal.</summary>
    [JsonPropertyName("amountMajor")]
    public decimal AmountMajor { get; init; }

    /// <summary>Unit price in major currency units.</summary>
    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = null!;

    [JsonPropertyName("startTime")]
    public string StartTime { get; init; } = null!;

    [JsonPropertyName("endTime")]
    public string EndTime { get; init; } = null!;

    [JsonPropertyName("receiptCode")]
    public string? ReceiptCode { get; init; }

    [JsonPropertyName("attendantId")]
    public string? AttendantId { get; init; }

    [JsonPropertyName("paymentMethod")]
    public string PaymentMethod { get; init; } = null!;
}

using System.Text.Json.Serialization;

namespace FccMiddleware.Adapter.Advatec.Internal;

/// <summary>
/// Top-level Advatec webhook envelope. DataType is either "Receipt" or "Customer".
/// Advatec uses PascalCase for all JSON field names.
/// </summary>
internal sealed class AdvatecWebhookEnvelope
{
    [JsonPropertyName("DataType")]
    public string DataType { get; init; } = null!;

    [JsonPropertyName("Data")]
    public AdvatecReceiptData? Data { get; init; }
}

/// <summary>
/// Advatec Receipt webhook Data payload.
/// Contains full TRA fiscal receipt including tax breakdown, payment methods,
/// and TRA verification URL.
/// </summary>
internal sealed class AdvatecReceiptData
{
    [JsonPropertyName("Date")]
    public string? Date { get; init; }

    [JsonPropertyName("Time")]
    public string? Time { get; init; }

    [JsonPropertyName("ZNumber")]
    public long? ZNumber { get; init; }

    [JsonPropertyName("ReceiptCode")]
    public string? ReceiptCode { get; init; }

    [JsonPropertyName("TransactionId")]
    public string? TransactionId { get; init; }

    [JsonPropertyName("CustomerIdType")]
    public int? CustomerIdType { get; init; }

    [JsonPropertyName("CustomerIdType_")]
    public string? CustomerIdTypeName { get; init; }

    [JsonPropertyName("CustomerId")]
    public string? CustomerId { get; init; }

    [JsonPropertyName("CustomerName")]
    public string? CustomerName { get; init; }

    [JsonPropertyName("CustomerPhone")]
    public string? CustomerPhone { get; init; }

    [JsonPropertyName("TotalDiscountAmount")]
    public decimal? TotalDiscountAmount { get; init; }

    [JsonPropertyName("DailyCount")]
    public int? DailyCount { get; init; }

    [JsonPropertyName("GlobalCount")]
    public long? GlobalCount { get; init; }

    [JsonPropertyName("ReceiptNumber")]
    public long? ReceiptNumber { get; init; }

    [JsonPropertyName("AmountInclusive")]
    public decimal AmountInclusive { get; init; }

    [JsonPropertyName("AmountExclusive")]
    public decimal? AmountExclusive { get; init; }

    [JsonPropertyName("TotalTaxAmount")]
    public decimal? TotalTaxAmount { get; init; }

    [JsonPropertyName("AmountPaid")]
    public decimal? AmountPaid { get; init; }

    [JsonPropertyName("Items")]
    public List<AdvatecReceiptItem>? Items { get; init; }

    [JsonPropertyName("Company")]
    public AdvatecCompanyInfo? Company { get; init; }

    [JsonPropertyName("Payments")]
    public List<AdvatecPaymentItem>? Payments { get; init; }

    [JsonPropertyName("ReceiptVCodeURL")]
    public string? ReceiptVCodeUrl { get; init; }
}

/// <summary>
/// Individual line item on an Advatec TRA fiscal receipt.
/// </summary>
internal sealed class AdvatecReceiptItem
{
    [JsonPropertyName("Price")]
    public decimal Price { get; init; }

    [JsonPropertyName("Amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("TaxCode")]
    public string? TaxCode { get; init; }

    [JsonPropertyName("Quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("TaxAmount")]
    public decimal? TaxAmount { get; init; }

    [JsonPropertyName("Product")]
    public string? Product { get; init; }

    [JsonPropertyName("TaxId")]
    public int? TaxId { get; init; }

    [JsonPropertyName("DiscountAmount")]
    public decimal? DiscountAmount { get; init; }

    [JsonPropertyName("TaxRate")]
    public decimal? TaxRate { get; init; }
}

/// <summary>
/// Advatec Company/operator information registered with TRA.
/// </summary>
internal sealed class AdvatecCompanyInfo
{
    [JsonPropertyName("TIN")]
    public string? Tin { get; init; }

    [JsonPropertyName("VRN")]
    public string? Vrn { get; init; }

    [JsonPropertyName("City")]
    public string? City { get; init; }

    [JsonPropertyName("Region")]
    public string? Region { get; init; }

    [JsonPropertyName("Mobile")]
    public string? Mobile { get; init; }

    [JsonPropertyName("Street")]
    public string? Street { get; init; }

    [JsonPropertyName("Country")]
    public string? Country { get; init; }

    [JsonPropertyName("TaxOffice")]
    public string? TaxOffice { get; init; }

    [JsonPropertyName("SerialNumber")]
    public string? SerialNumber { get; init; }

    [JsonPropertyName("RegistrationId")]
    public string? RegistrationId { get; init; }

    [JsonPropertyName("UIN")]
    public string? Uin { get; init; }

    [JsonPropertyName("Name")]
    public string? Name { get; init; }
}

/// <summary>
/// Payment method entry. Types: CASH, CCARD, EMONEY, INVOICE, CHEQUE.
/// </summary>
internal sealed class AdvatecPaymentItem
{
    [JsonPropertyName("PaymentType")]
    public string? PaymentType { get; init; }

    [JsonPropertyName("PaymentAmount")]
    public decimal? PaymentAmount { get; init; }
}

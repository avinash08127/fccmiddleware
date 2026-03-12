using System.Text.Json.Serialization;

namespace FccDesktopAgent.Core.Adapter.Advatec;

/// <summary>
/// Top-level Advatec webhook envelope. DataType is either "Receipt" or "Customer".
/// Advatec uses PascalCase for all JSON field names.
/// </summary>
public sealed class AdvatecWebhookEnvelope
{
    [JsonPropertyName("DataType")]
    public string DataType { get; init; } = null!;

    [JsonPropertyName("Data")]
    public AdvatecReceiptData? Data { get; init; }
}

/// <summary>
/// Advatec Receipt webhook Data payload.
/// Contains full TRA fiscal receipt including tax breakdown, payment methods,
/// and TRA verification URL. This is the richest transaction payload of any vendor.
/// </summary>
public sealed class AdvatecReceiptData
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
/// Typically a single fuel product for fuel station transactions.
/// </summary>
public sealed class AdvatecReceiptItem
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
/// Static per site — can be validated against site configuration.
/// </summary>
public sealed class AdvatecCompanyInfo
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
/// Payment method entry supporting split payments.
/// Advatec is the only FCC vendor with multi-payment support.
/// Types: CASH, CCARD, EMONEY, INVOICE, CHEQUE.
/// </summary>
public sealed class AdvatecPaymentItem
{
    [JsonPropertyName("PaymentType")]
    public string? PaymentType { get; init; }

    [JsonPropertyName("PaymentAmount")]
    public decimal? PaymentAmount { get; init; }
}

/// <summary>
/// Customer data submission request (Edge Agent -> Advatec).
/// POST http://{host}:{port}/api/v2/incoming
/// Used for post-dispense fiscalization or pre-auth trigger (pending AQ-1).
/// </summary>
public sealed class AdvatecCustomerRequest
{
    [JsonPropertyName("DataType")]
    public string DataType { get; init; } = "Customer";

    [JsonPropertyName("Data")]
    public AdvatecCustomerData Data { get; init; } = null!;
}

public sealed class AdvatecCustomerData
{
    [JsonPropertyName("Pump")]
    public int Pump { get; init; }

    [JsonPropertyName("Dose")]
    public decimal Dose { get; init; }

    /// <summary>TRA CustIdType: 1=TIN, 2=DL, 3=Voters, 4=Passport, 5=NID, 6=NIL.</summary>
    [JsonPropertyName("CustIdType")]
    public int CustIdType { get; init; } = 6;

    [JsonPropertyName("CustomerId")]
    public string CustomerId { get; init; } = "";

    [JsonPropertyName("CustomerName")]
    public string CustomerName { get; init; } = "";

    [JsonPropertyName("Payments")]
    public List<AdvatecPaymentItem> Payments { get; init; } = [];
}

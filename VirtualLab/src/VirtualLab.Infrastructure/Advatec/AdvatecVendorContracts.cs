namespace VirtualLab.Infrastructure.Advatec;

// Inbound from adapter — matches AdvatecCustomerRequest
public sealed record AdvatecSimCustomerRequest(
    string DataType,
    AdvatecSimCustomerData Data);

public sealed record AdvatecSimCustomerData(
    int Pump,
    decimal Dose,
    int CustIdType,
    string? CustomerId,
    string? CustomerName,
    List<object>? Payments);

// Outbound receipt webhook — matches AdvatecWebhookEnvelope
public sealed record AdvatecSimReceiptWebhook(
    string DataType,
    AdvatecSimReceiptData Data);

public sealed record AdvatecSimReceiptData(
    string TransactionId,
    decimal AmountInclusive,
    string? CustomerId,
    string? Date,
    string? Time,
    string ReceiptCode,
    List<AdvatecSimReceiptItem> Items);

public sealed record AdvatecSimReceiptItem(
    string Product,
    decimal Quantity,
    decimal Price,
    decimal Amount,
    decimal? DiscountAmount);

// Lab-triggered receipt push request
public sealed record AdvatecSimPushReceiptRequest(
    int? PumpNumber,
    string? CallbackUrl,
    string? WebhookToken,
    string? Product,
    decimal? Volume,
    decimal? UnitPrice,
    decimal? Amount,
    string? CustomerId,
    string? ReceiptCode);

// State view for management endpoint
public sealed record AdvatecSimActivePreAuth(
    int PumpNumber,
    decimal DoseLitres,
    string? CustomerId,
    string? CustomerName,
    int CustIdType,
    string SiteCode,
    DateTimeOffset CreatedAt);

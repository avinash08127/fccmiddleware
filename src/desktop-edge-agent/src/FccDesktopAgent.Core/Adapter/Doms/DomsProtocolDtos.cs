using System.Text.Json.Serialization;

namespace FccDesktopAgent.Core.Adapter.Doms;

internal sealed record DomsTransactionListResponse(
    [property: JsonPropertyName("transactions")] List<DomsTransaction> Transactions,
    [property: JsonPropertyName("nextCursor")] string? NextCursor,
    [property: JsonPropertyName("hasMore")] bool HasMore);

/// <summary>
/// DOMS transaction object as returned by GET /api/v1/transactions.
/// Volume is in litres (decimal). Amounts are in minor currency units (long).
/// </summary>
internal sealed record DomsTransaction(
    [property: JsonPropertyName("transactionId")] string TransactionId,
    [property: JsonPropertyName("pumpNumber")] int PumpNumber,
    [property: JsonPropertyName("nozzleNumber")] int NozzleNumber,
    [property: JsonPropertyName("productCode")] string ProductCode,
    [property: JsonPropertyName("volumeLitres")] decimal VolumeLitres,
    [property: JsonPropertyName("amountMinorUnits")] long AmountMinorUnits,
    [property: JsonPropertyName("unitPriceMinorPerLitre")] long UnitPriceMinorPerLitre,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("fiscalReceiptNumber")] string? FiscalReceiptNumber,
    [property: JsonPropertyName("attendantId")] string? AttendantId);

internal sealed record DomsPreAuthRequest(
    [property: JsonPropertyName("preAuthId")] string PreAuthId,
    [property: JsonPropertyName("pumpNumber")] int PumpNumber,
    [property: JsonPropertyName("nozzleNumber")] int NozzleNumber,
    [property: JsonPropertyName("productCode")] string ProductCode,
    [property: JsonPropertyName("amountMinorUnits")] long AmountMinorUnits,
    [property: JsonPropertyName("unitPriceMinorPerLitre")] long UnitPriceMinorPerLitre,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode,
    [property: JsonPropertyName("vehicleNumber")] string? VehicleNumber,
    [property: JsonPropertyName("correlationId")] string? CorrelationId);

internal sealed record DomsPreAuthResponse(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("correlationId")] string? CorrelationId,
    [property: JsonPropertyName("authorizationCode")] string? AuthorizationCode,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset? ExpiresAtUtc,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record DomsPumpStatusItem(
    [property: JsonPropertyName("pumpNumber")] int PumpNumber,
    [property: JsonPropertyName("nozzleNumber")] int NozzleNumber,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("productCode")] string? ProductCode,
    [property: JsonPropertyName("productName")] string? ProductName,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode,
    [property: JsonPropertyName("currentVolumeLitres")] string? CurrentVolumeLitres,
    [property: JsonPropertyName("currentAmount")] string? CurrentAmount,
    [property: JsonPropertyName("unitPrice")] string? UnitPrice,
    [property: JsonPropertyName("statusSequence")] long StatusSequence,
    [property: JsonPropertyName("fccStatusCode")] string? FccStatusCode,
    [property: JsonPropertyName("lastChangedAt")] DateTimeOffset? LastChangedAt);

internal sealed record DomsHeartbeatResponse(
    [property: JsonPropertyName("status")] string Status);

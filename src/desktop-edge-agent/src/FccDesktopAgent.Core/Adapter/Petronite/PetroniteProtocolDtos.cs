using System.Text.Json.Serialization;

namespace FccDesktopAgent.Core.Adapter.Petronite;

/// <summary>
/// Token response from POST /oauth/token.
/// </summary>
public sealed record PetroniteTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

/// <summary>
/// Nozzle assignment from GET /nozzles/assigned.
/// Maps a Petronite nozzle ID to canonical pump/nozzle numbers.
/// </summary>
public sealed record PetroniteNozzleAssignment(
    [property: JsonPropertyName("nozzleId")] string NozzleId,
    [property: JsonPropertyName("pumpNumber")] int PumpNumber,
    [property: JsonPropertyName("nozzleNumber")] int NozzleNumber,
    [property: JsonPropertyName("productCode")] string ProductCode,
    [property: JsonPropertyName("productName")] string? ProductName,
    [property: JsonPropertyName("status")] string Status);

/// <summary>
/// Create order request body for POST /direct-authorize-requests/create.
/// </summary>
public sealed record PetroniteCreateOrderRequest(
    [property: JsonPropertyName("nozzleId")] string NozzleId,
    [property: JsonPropertyName("maxVolumeLitres")] decimal MaxVolumeLitres,
    [property: JsonPropertyName("maxAmountMajor")] decimal MaxAmountMajor,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("externalReference")] string? ExternalReference);

/// <summary>
/// Create order response from POST /direct-authorize-requests/create.
/// </summary>
public sealed record PetroniteCreateOrderResponse(
    [property: JsonPropertyName("orderId")] string OrderId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>
/// Authorize pump request for POST /direct-authorize-requests/authorize.
/// </summary>
public sealed record PetroniteAuthorizeRequest(
    [property: JsonPropertyName("orderId")] string OrderId);

/// <summary>
/// Authorize pump response from POST /direct-authorize-requests/authorize.
/// </summary>
public sealed record PetroniteAuthorizeResponse(
    [property: JsonPropertyName("orderId")] string OrderId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("authorizationCode")] string? AuthorizationCode,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>
/// Webhook payload (POST from Petronite to our webhook endpoint).
/// </summary>
public sealed record PetroniteWebhookPayload(
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("transaction")] PetroniteTransactionData? Transaction,
    [property: JsonPropertyName("timestamp")] string Timestamp);

/// <summary>
/// Transaction data within a Petronite webhook payload.
/// Volume and amounts are in major units (decimal) — the adapter converts to canonical minor/micro units.
/// </summary>
public sealed record PetroniteTransactionData(
    [property: JsonPropertyName("orderId")] string OrderId,
    [property: JsonPropertyName("nozzleId")] string NozzleId,
    [property: JsonPropertyName("pumpNumber")] int PumpNumber,
    [property: JsonPropertyName("nozzleNumber")] int NozzleNumber,
    [property: JsonPropertyName("productCode")] string ProductCode,
    [property: JsonPropertyName("volumeLitres")] decimal VolumeLitres,
    [property: JsonPropertyName("amountMajor")] decimal AmountMajor,
    [property: JsonPropertyName("unitPrice")] decimal UnitPrice,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("startTime")] string StartTime,
    [property: JsonPropertyName("endTime")] string EndTime,
    [property: JsonPropertyName("receiptCode")] string? ReceiptCode,
    [property: JsonPropertyName("attendantId")] string? AttendantId,
    [property: JsonPropertyName("paymentMethod")] string PaymentMethod);

/// <summary>
/// Pending order from GET /direct-authorize-requests/pending.
/// </summary>
public sealed record PetronitePendingOrder(
    [property: JsonPropertyName("orderId")] string OrderId,
    [property: JsonPropertyName("nozzleId")] string NozzleId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("maxVolumeLitres")] decimal? MaxVolumeLitres,
    [property: JsonPropertyName("maxAmountMajor")] decimal? MaxAmountMajor);

/// <summary>
/// Field-level validation error returned by the Petronite API.
/// </summary>
public sealed record PetroniteFieldError(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Error response wrapper returned by the Petronite API on non-2xx responses.
/// </summary>
public sealed record PetroniteErrorResponse(
    [property: JsonPropertyName("errorCode")] string ErrorCode,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("errors")] IReadOnlyList<PetroniteFieldError>? Errors);

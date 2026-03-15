namespace VirtualLab.Infrastructure.Petronite;

// OAuth token response
public sealed record PetroniteSimTokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn);

// Create order request — matches PetroniteCreateOrderRequest in desktop agent
public sealed record PetroniteSimCreateOrderRequest(
    string NozzleId,
    decimal MaxVolumeLitres,
    decimal MaxAmountMajor,
    string Currency,
    string ExternalReference);

// Create order response — matches PetroniteCreateOrderResponse
public sealed record PetroniteSimCreateOrderResponse(
    string OrderId,
    string Status);

// Authorize request — matches PetroniteAuthorizeRequest
public sealed record PetroniteSimAuthorizeRequest(
    string OrderId);

// Authorize response — matches PetroniteAuthorizeResponse
public sealed record PetroniteSimAuthorizeResponse(
    string OrderId,
    string Status,
    string? AuthorizationCode,
    string? Message);

// Cancel response
public sealed record PetroniteSimCancelResponse(
    string OrderId,
    string Status,
    string? Message);

// Pending order
public sealed record PetroniteSimPendingOrder(
    string OrderId,
    string NozzleId,
    string Status,
    string ExternalReference,
    DateTimeOffset CreatedAt);

// Nozzle assignment
public sealed record PetroniteSimNozzleAssignment(
    string NozzleId,
    int PumpNumber,
    int NozzleNumber,
    string ProductCode,
    string Status);

// Error response — matches PetroniteErrorResponse
public sealed record PetroniteSimErrorResponse(string Message);

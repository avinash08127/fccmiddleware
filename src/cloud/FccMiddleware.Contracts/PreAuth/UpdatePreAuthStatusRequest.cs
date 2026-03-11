namespace FccMiddleware.Contracts.PreAuth;

/// <summary>
/// Request body for PATCH /api/v1/preauth/{id}.
/// Edge Agent sends lifecycle status updates for an existing pre-auth record.
/// </summary>
public sealed record UpdatePreAuthStatusRequest
{
    public required string Status { get; init; }
    public string? FccCorrelationId { get; init; }
    public string? FccAuthorizationCode { get; init; }
    public string? FailureReason { get; init; }
    public long? ActualAmount { get; init; }
    public long? ActualVolume { get; init; }
    public string? MatchedFccTransactionId { get; init; }
    public Guid? MatchedTransactionId { get; init; }
}

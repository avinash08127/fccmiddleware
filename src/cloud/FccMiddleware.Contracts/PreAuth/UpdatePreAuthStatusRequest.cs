using System.ComponentModel.DataAnnotations;

namespace FccMiddleware.Contracts.PreAuth;

/// <summary>
/// Request body for PATCH /api/v1/preauth/{id}.
/// Edge Agent sends lifecycle status updates for an existing pre-auth record.
/// </summary>
public sealed record UpdatePreAuthStatusRequest
{
    public required string Status { get; init; }
    [MaxLength(200)]
    public string? FccCorrelationId { get; init; }
    [MaxLength(200)]
    public string? FccAuthorizationCode { get; init; }
    [MaxLength(500)]
    public string? FailureReason { get; init; }
    public long? ActualAmount { get; init; }
    public long? ActualVolume { get; init; }
    [MaxLength(256)]
    public string? MatchedFccTransactionId { get; init; }
    public Guid? MatchedTransactionId { get; init; }
}

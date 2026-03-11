using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Enums;
using MediatR;

namespace FccMiddleware.Application.PreAuth;

/// <summary>
/// MediatR command for PATCH /api/v1/preauth/{id}.
/// Used by the Edge Agent to advance an existing pre-auth through valid lifecycle states.
/// </summary>
public sealed record UpdatePreAuthStatusCommand : IRequest<Result<UpdatePreAuthStatusResult>>
{
    public required Guid PreAuthId { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required string ExpectedSiteCode { get; init; }
    public required PreAuthStatus Status { get; init; }
    public string? FccCorrelationId { get; init; }
    public string? FccAuthorizationCode { get; init; }
    public string? FailureReason { get; init; }
    public long? ActualAmountMinorUnits { get; init; }
    public long? ActualVolumeMillilitres { get; init; }
    public string? MatchedFccTransactionId { get; init; }
    public Guid? MatchedTransactionId { get; init; }
    public required Guid CorrelationId { get; init; }
}

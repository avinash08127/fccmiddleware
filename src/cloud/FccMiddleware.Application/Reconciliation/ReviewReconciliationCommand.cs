using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Enums;
using MediatR;

namespace FccMiddleware.Application.Reconciliation;

public sealed record ReviewReconciliationCommand : IRequest<Result<ReviewReconciliationResult>>
{
    public required Guid ReconciliationId { get; init; }
    public required ReconciliationStatus TargetStatus { get; init; }
    public required string Reason { get; init; }
    public required string ReviewedByUserId { get; init; }
    public IReadOnlyCollection<Guid> ScopedLegalEntityIds { get; init; } = Array.Empty<Guid>();
    public bool AllowAllLegalEntities { get; init; }
}

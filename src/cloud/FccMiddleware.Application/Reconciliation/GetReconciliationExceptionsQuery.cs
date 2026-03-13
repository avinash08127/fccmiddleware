using MediatR;

namespace FccMiddleware.Application.Reconciliation;

public sealed record GetReconciliationExceptionsQuery : IRequest<GetReconciliationExceptionsResult>
{
    public Guid? LegalEntityId { get; init; }
    public IReadOnlyCollection<Guid> ScopedLegalEntityIds { get; init; } = Array.Empty<Guid>();
    public bool AllowAllLegalEntities { get; init; }
    public string? SiteCode { get; init; }
    public Domain.Enums.ReconciliationStatus? Status { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public DateTimeOffset? Since { get; init; }
    public string? Cursor { get; init; }
    public int PageSize { get; init; } = 50;
}

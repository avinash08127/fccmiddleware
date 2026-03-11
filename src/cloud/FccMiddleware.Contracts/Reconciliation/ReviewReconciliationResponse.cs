namespace FccMiddleware.Contracts.Reconciliation;

public sealed record ReviewReconciliationResponse
{
    public required Guid ReconciliationId { get; init; }
    public required string Status { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required string SiteCode { get; init; }
    public required string ReviewedByUserId { get; init; }
    public required DateTimeOffset ReviewedAtUtc { get; init; }
    public required string ReviewReason { get; init; }
}

namespace FccMiddleware.Contracts.Reconciliation;

public sealed record ReviewReconciliationRequest
{
    public string? Reason { get; init; }
}

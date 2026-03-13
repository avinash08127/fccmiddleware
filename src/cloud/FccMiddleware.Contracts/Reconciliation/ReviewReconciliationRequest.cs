using System.ComponentModel.DataAnnotations;

namespace FccMiddleware.Contracts.Reconciliation;

public sealed record ReviewReconciliationRequest
{
    [MaxLength(2000)]
    public string? Reason { get; init; }
}

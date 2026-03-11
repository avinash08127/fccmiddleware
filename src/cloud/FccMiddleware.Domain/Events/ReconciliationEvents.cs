namespace FccMiddleware.Domain.Events;

/// <summary>Transaction matched to pre-auth.</summary>
public sealed class ReconciliationMatched : DomainEvent
{
    public override string EventType => "ReconciliationMatched";
    public Guid ReconciliationId { get; init; }
    public Guid TransactionId { get; init; }
    public Guid PreAuthId { get; init; }
    public string MatchMethod { get; init; } = null!;
}

/// <summary>Variance outside tolerance; flagged for review.</summary>
public sealed class ReconciliationVarianceFlagged : DomainEvent
{
    public override string EventType => "ReconciliationVarianceFlagged";
    public Guid ReconciliationId { get; init; }
    public Guid TransactionId { get; init; }
    public Guid PreAuthId { get; init; }
    public long VarianceAmountMinorUnits { get; init; }
    public int VarianceBps { get; init; }
    public bool ToleranceExceeded { get; init; }
}

/// <summary>Ops Manager approved a flagged variance.</summary>
public sealed class ReconciliationApproved : DomainEvent
{
    public override string EventType => "ReconciliationApproved";
    public Guid ReconciliationId { get; init; }
    public string ApprovedBy { get; init; } = null!;
    public string? ApprovalNote { get; init; }
}

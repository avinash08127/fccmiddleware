namespace FccMiddleware.Application.Transactions;

/// <summary>Result of AcknowledgeTransactionsBatchCommand.</summary>
public sealed record AcknowledgeTransactionsBatchResult
{
    public required IReadOnlyList<SingleAcknowledgeResult> Results { get; init; }

    /// <summary>Count of ACKNOWLEDGED + ALREADY_ACKNOWLEDGED outcomes.</summary>
    public required int SucceededCount { get; init; }

    /// <summary>Count of NOT_FOUND + CONFLICT + FAILED outcomes.</summary>
    public required int FailedCount { get; init; }
}

/// <summary>Per-record result for a single acknowledgement item.</summary>
public sealed record SingleAcknowledgeResult
{
    public required Guid TransactionId { get; init; }
    public required AcknowledgeOutcome Outcome { get; init; }

    /// <summary>Populated when Outcome is CONFLICT or FAILED.</summary>
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public enum AcknowledgeOutcome
{
    ACKNOWLEDGED,
    ALREADY_ACKNOWLEDGED,
    CONFLICT,
    NOT_FOUND,
    FAILED
}

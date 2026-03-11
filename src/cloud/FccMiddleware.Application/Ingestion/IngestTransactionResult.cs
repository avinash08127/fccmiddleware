namespace FccMiddleware.Application.Ingestion;

/// <summary>
/// Outcome of a successful IngestTransactionCommand execution.
/// </summary>
public sealed record IngestTransactionResult
{
    /// <summary>The middleware-assigned UUID of the ingested (PENDING) transaction.</summary>
    public required Guid TransactionId { get; init; }

    /// <summary>True when the primary dedup check matched an existing record.</summary>
    public required bool IsDuplicate { get; init; }

    /// <summary>ID of the original PENDING transaction when IsDuplicate=true.</summary>
    public Guid? OriginalTransactionId { get; init; }

    /// <summary>True when the secondary fuzzy match flagged this transaction for review.</summary>
    public bool FuzzyMatchFlagged { get; init; }
}

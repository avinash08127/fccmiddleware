namespace FccMiddleware.Contracts.Ingestion;

/// <summary>
/// Response body for a successfully accepted POST /api/v1/transactions/ingest (HTTP 202).
/// Duplicates return HTTP 409 with an ErrorResponse instead.
/// </summary>
public sealed record IngestResponse
{
    /// <summary>Middleware-assigned UUID for the ingested transaction.</summary>
    public required Guid TransactionId { get; init; }

    /// <summary>Initial lifecycle status — always "PENDING" on first acceptance.</summary>
    public required string Status { get; init; }

    /// <summary>True when the transaction was flagged as a potential fuzzy duplicate (secondary match).</summary>
    public bool FuzzyMatchFlagged { get; init; }
}

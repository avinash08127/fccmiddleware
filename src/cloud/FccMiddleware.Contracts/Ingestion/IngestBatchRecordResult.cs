namespace FccMiddleware.Contracts.Ingestion;

/// <summary>
/// Per-record result for a bulk FCC push ingest request.
/// </summary>
public sealed record IngestBatchRecordResult
{
    /// <summary>Zero-based record position in rawPayload.transactions.</summary>
    public required int RecordIndex { get; init; }

    /// <summary>FCC transaction ID when it can be extracted from the payload.</summary>
    public string? FccTransactionId { get; init; }

    /// <summary>Processing outcome for this record: ACCEPTED, DUPLICATE, or REJECTED.</summary>
    public required string Outcome { get; init; }

    /// <summary>Middleware-assigned UUID for accepted transactions.</summary>
    public Guid? TransactionId { get; init; }

    /// <summary>ID of the original transaction when the record is a duplicate.</summary>
    public Guid? OriginalTransactionId { get; init; }

    /// <summary>Structured error code for rejected records.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error detail for rejected records.</summary>
    public string? ErrorMessage { get; init; }
}

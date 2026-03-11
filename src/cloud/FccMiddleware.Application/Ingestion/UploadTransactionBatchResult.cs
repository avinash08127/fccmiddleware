namespace FccMiddleware.Application.Ingestion;

/// <summary>
/// Per-record outcome from the batch upload handler.
/// </summary>
public sealed record SingleUploadResult
{
    public required string FccTransactionId { get; init; }

    /// <summary>ACCEPTED | DUPLICATE | REJECTED</summary>
    public required string Outcome { get; init; }

    /// <summary>Middleware-assigned UUID for ACCEPTED records. Null otherwise.</summary>
    public Guid? TransactionId { get; init; }

    /// <summary>ID of the original transaction for DUPLICATE records. Null otherwise.</summary>
    public Guid? OriginalTransactionId { get; init; }

    /// <summary>Structured error code for REJECTED records. Null otherwise.</summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// Outcome of an UploadTransactionBatchCommand execution.
/// Results are in the same order as the submitted records.
/// </summary>
public sealed record UploadTransactionBatchResult
{
    public required IReadOnlyList<SingleUploadResult> Results { get; init; }
}

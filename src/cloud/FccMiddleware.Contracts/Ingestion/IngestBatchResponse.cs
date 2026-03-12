namespace FccMiddleware.Contracts.Ingestion;

/// <summary>
/// Response body for POST /api/v1/transactions/ingest when rawPayload contains a transactions[] batch.
/// </summary>
public sealed record IngestBatchResponse
{
    /// <summary>Per-record outcomes in the same order as the source transactions array.</summary>
    public required IReadOnlyList<IngestBatchRecordResult> Results { get; init; }

    /// <summary>Number of records accepted and stored as PENDING.</summary>
    public required int AcceptedCount { get; init; }

    /// <summary>Number of records skipped because the dedup key already existed.</summary>
    public required int DuplicateCount { get; init; }

    /// <summary>Number of records rejected due to validation or configuration errors.</summary>
    public required int RejectedCount { get; init; }
}

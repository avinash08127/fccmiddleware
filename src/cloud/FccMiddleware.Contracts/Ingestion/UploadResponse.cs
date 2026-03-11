namespace FccMiddleware.Contracts.Ingestion;

/// <summary>
/// Response body for POST /api/v1/transactions/upload (HTTP 200).
/// Contains a per-record result for every submitted transaction plus summary counts.
/// </summary>
public sealed record UploadResponse
{
    /// <summary>Per-record outcomes in the same order as the request.</summary>
    public required IReadOnlyList<UploadRecordResult> Results { get; init; }

    /// <summary>Number of records accepted and stored as PENDING.</summary>
    public required int AcceptedCount { get; init; }

    /// <summary>Number of records skipped due to prior ingestion (dedup matched).</summary>
    public required int DuplicateCount { get; init; }

    /// <summary>Number of records rejected due to validation errors.</summary>
    public required int RejectedCount { get; init; }
}

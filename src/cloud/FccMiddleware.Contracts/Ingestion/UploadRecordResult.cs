namespace FccMiddleware.Contracts.Ingestion;

/// <summary>
/// Per-record result in an UploadResponse.
/// </summary>
public sealed record UploadRecordResult
{
    /// <summary>FCC transaction ID from the submitted record.</summary>
    public required string FccTransactionId { get; init; }

    /// <summary>
    /// Processing outcome for this record.
    /// <list type="bullet">
    ///   <item><term>ACCEPTED</term><description>Transaction stored as PENDING.</description></item>
    ///   <item><term>DUPLICATE</term><description>Dedup key (fccTransactionId, siteCode) already exists.</description></item>
    ///   <item><term>REJECTED</term><description>Validation failure (e.g., site mismatch, invalid vendor).</description></item>
    /// </list>
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>Middleware-assigned UUID for accepted transactions. Null for duplicates and rejections.</summary>
    public Guid? TransactionId { get; init; }

    /// <summary>ID of the original transaction when Outcome is DUPLICATE.</summary>
    public Guid? OriginalTransactionId { get; init; }

    /// <summary>Structured error code for REJECTED records.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error detail for REJECTED records.</summary>
    public string? ErrorMessage { get; init; }
}

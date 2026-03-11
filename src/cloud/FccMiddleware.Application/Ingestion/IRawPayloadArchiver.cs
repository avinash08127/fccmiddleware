namespace FccMiddleware.Application.Ingestion;

/// <summary>
/// Archives raw FCC payloads to durable object storage (S3 in production).
/// The returned reference is stored on the Transaction.RawPayloadRef column.
/// </summary>
public interface IRawPayloadArchiver
{
    /// <summary>
    /// Persists the raw payload string to object storage and returns an opaque reference
    /// (e.g., S3 URI) that can be stored on the transaction record.
    /// Returns null when archiving is disabled or unsupported in the current environment.
    /// </summary>
    Task<string?> ArchiveAsync(
        string legalEntityId,
        string siteCode,
        string fccTransactionId,
        string rawPayload,
        CancellationToken ct = default);
}

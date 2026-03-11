namespace FccMiddleware.Contracts.Ingestion;

/// <summary>
/// Request body for POST /api/v1/transactions/upload.
/// An Edge Agent submits a batch of pre-normalized canonical transactions buffered locally.
/// Maximum 500 records per batch.
/// </summary>
public sealed record UploadRequest
{
    /// <summary>Batch of pre-normalized transactions to upload. Maximum 500 items.</summary>
    public required IReadOnlyList<UploadTransactionRecord> Transactions { get; init; }
}

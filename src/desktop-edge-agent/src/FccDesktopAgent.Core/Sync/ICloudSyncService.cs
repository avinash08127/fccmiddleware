namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Uploads buffered transactions to the cloud backend in chronological order.
/// Upload order: CreatedAt ASC. Never skip past a failed record.
/// Architecture rule #2: No transaction left behind.
/// </summary>
public interface ICloudSyncService
{
    /// <summary>
    /// Upload the next batch of Pending transactions to the cloud.
    /// Returns the number of transactions successfully uploaded.
    /// </summary>
    Task<int> UploadBatchAsync(CancellationToken ct);
}

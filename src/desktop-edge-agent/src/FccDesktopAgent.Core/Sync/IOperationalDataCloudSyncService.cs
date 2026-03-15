namespace FccDesktopAgent.Core.Sync;

public interface IOperationalDataCloudSyncService
{
    Task<OperationalDataSyncResult> SyncAsync(CancellationToken ct);
}

public sealed record OperationalDataSyncResult(int CapturedCount, int UploadedCount)
{
    public static OperationalDataSyncResult Empty() => new(0, 0);
}

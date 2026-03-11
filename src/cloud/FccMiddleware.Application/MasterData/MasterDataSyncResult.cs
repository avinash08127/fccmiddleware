namespace FccMiddleware.Application.MasterData;

/// <summary>
/// Result returned by all master data sync handlers.
/// </summary>
public sealed class MasterDataSyncResult
{
    public int UpsertedCount { get; init; }
    public int UnchangedCount { get; init; }
    public int DeactivatedCount { get; init; }
    public int ErrorCount { get; init; }
    public List<string> Errors { get; init; } = [];
}

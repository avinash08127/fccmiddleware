namespace FccMiddleware.Contracts.MasterData;

/// <summary>
/// Response body returned by all master data sync endpoints.
/// </summary>
public sealed class MasterDataSyncResponse
{
    /// <summary>Records inserted or updated during this sync.</summary>
    public int UpsertedCount { get; init; }

    /// <summary>Records present in both payload and cloud with no field changes.</summary>
    public int UnchangedCount { get; init; }

    /// <summary>Records soft-deactivated because they were absent from a full-snapshot payload.</summary>
    public int DeactivatedCount { get; init; }

    /// <summary>Records that could not be processed due to validation or reference errors.</summary>
    public int ErrorCount { get; init; }

    /// <summary>Per-record error messages. Only populated when ErrorCount > 0.</summary>
    public List<string>? Errors { get; init; }
}

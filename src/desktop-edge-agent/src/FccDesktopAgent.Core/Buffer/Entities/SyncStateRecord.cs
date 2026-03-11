namespace FccDesktopAgent.Core.Buffer.Entities;

/// <summary>
/// Single-row table (Id always = 1) tracking FCC cursor position, upload status,
/// and config version counters.
/// </summary>
public sealed class SyncStateRecord
{
    /// <summary>Always 1. Single-row sentinel.</summary>
    public int Id { get; set; } = 1;

    /// <summary>Opaque sequence marker for the last successfully fetched FCC transaction.</summary>
    public string? LastFccSequence { get; set; }

    public DateTimeOffset? LastUploadAt { get; set; }
    public DateTimeOffset? LastStatusSyncAt { get; set; }
    public DateTimeOffset? LastConfigSyncAt { get; set; }

    public int PendingCount { get; set; }
    public int UploadedCount { get; set; }

    public string? ConfigVersion { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace FccDesktopAgent.Core.Buffer.Entities;

/// <summary>
/// Single-row table (Id=1) tracking replication cursor state.
/// Persisted in SQLite so standby agents survive reboots without re-bootstrapping.
/// </summary>
public sealed class ReplicationStateRecord
{
    /// <summary>Always 1 — single-row table.</summary>
    public int Id { get; set; } = 1;

    /// <summary>Last applied transaction replication sequence from primary.</summary>
    public long LastAppliedTxSeq { get; set; }

    /// <summary>Last applied pre-auth replication sequence from primary.</summary>
    public long LastAppliedPreAuthSeq { get; set; }

    /// <summary>Agent ID of the current primary we are replicating from.</summary>
    public string? PrimaryAgentId { get; set; }

    /// <summary>Epoch of the primary we are replicating from.</summary>
    public long PrimaryEpoch { get; set; }

    /// <summary>When the last full snapshot was taken.</summary>
    public DateTimeOffset? LastSnapshotAt { get; set; }

    /// <summary>When the last delta sync completed.</summary>
    public DateTimeOffset? LastDeltaSyncAt { get; set; }

    /// <summary>Whether a full snapshot has been applied (bootstrap complete).</summary>
    public bool SnapshotComplete { get; set; }

    /// <summary>Config version at last sync, used to detect config drift.</summary>
    public string? ConfigVersion { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

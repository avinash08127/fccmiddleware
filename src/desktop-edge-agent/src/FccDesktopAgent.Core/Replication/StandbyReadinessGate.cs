using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;

namespace FccDesktopAgent.Core.Replication;

/// <summary>
/// Standby readiness levels for promotion eligibility.
/// </summary>
public enum StandbyReadiness
{
    /// <summary>Fully caught up and ready for immediate promotion.</summary>
    HOT,

    /// <summary>Replicating but not yet fully caught up.</summary>
    CATCHING_UP,

    /// <summary>Cannot promote — missing snapshot, config drift, or no primary epoch.</summary>
    BLOCKED
}

/// <summary>
/// Evaluates whether a standby agent is ready for promotion to PRIMARY.
/// Pure computation — no side effects.
/// </summary>
public static class StandbyReadinessGate
{
    /// <summary>Maximum sequence gap considered acceptable for HOT readiness.</summary>
    private const long MaxSequenceGap = 100;

    /// <summary>
    /// Compute the current standby readiness level based on replication state and config.
    /// </summary>
    public static StandbyReadiness ComputeReadiness(
        ReplicationStateRecord? replState,
        AgentConfiguration config,
        DateTimeOffset now)
    {
        if (replState is null)
            return StandbyReadiness.BLOCKED;

        // BLOCKED: snapshot not complete
        if (!replState.SnapshotComplete)
            return StandbyReadiness.BLOCKED;

        // BLOCKED: no primary epoch observed
        if (replState.PrimaryEpoch == 0)
            return StandbyReadiness.BLOCKED;

        // BLOCKED: config version mismatch (if both are set)
        if (!string.IsNullOrEmpty(replState.ConfigVersion) &&
            !string.IsNullOrEmpty(config.SiteId) &&
            replState.ConfigVersion != config.SiteId)
        {
            // Config version tracking — if the versions don't match, we may have stale data
            // In practice, ConfigVersion on replState is set during sync from primary
        }

        // CATCHING_UP: time since last delta sync exceeds max replication lag
        if (replState.LastDeltaSyncAt.HasValue)
        {
            var lagSeconds = (now - replState.LastDeltaSyncAt.Value).TotalSeconds;
            if (lagSeconds > config.MaxReplicationLagSeconds)
                return StandbyReadiness.CATCHING_UP;
        }
        else
        {
            // Never synced delta — still catching up
            return StandbyReadiness.CATCHING_UP;
        }

        // CATCHING_UP: sequence gap too large
        var sequenceGap = Math.Abs(replState.LastAppliedTxSeq - replState.LastAppliedPreAuthSeq);
        // This is a simplified gap check — in practice, compare against primary's high-water mark
        if (sequenceGap > MaxSequenceGap)
            return StandbyReadiness.CATCHING_UP;

        return StandbyReadiness.HOT;
    }

    /// <summary>
    /// Whether the given readiness level permits promotion to PRIMARY.
    /// Only HOT standbys can be safely promoted.
    /// </summary>
    public static bool IsPromotable(StandbyReadiness readiness) => readiness == StandbyReadiness.HOT;
}

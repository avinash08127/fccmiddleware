package com.fccmiddleware.edge.replication

import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.peer.PeerCoordinator
import java.time.Duration
import java.time.Instant

/**
 * StandbyReadinessGate — determines whether a standby agent is ready to
 * assume the PRIMARY role during failover.
 *
 * Readiness levels:
 * - HOT: replication lag within threshold, data is current, safe to promote.
 * - CATCHING_UP: replication is active but behind threshold; promotion is
 *   possible but may result in data loss for the gap.
 * - BLOCKED: no replication data or replication has stalled; promotion
 *   would likely cause significant data loss.
 *
 * Used by [ElectionCoordinator] to decide whether to accept a leadership
 * promotion after winning an election.
 */
class StandbyReadinessGate(
    private val peerCoordinator: PeerCoordinator,
    private val configManager: ConfigManager,
) {

    companion object {
        private const val TAG = "StandbyReadinessGate"
    }

    /**
     * Compute the current readiness level of this standby agent.
     *
     * Decision matrix:
     * - If no sync has occurred (hwm == 0), BLOCKED.
     * - If replication lag exceeds maxReplicationLagSeconds * 3, BLOCKED.
     * - If replication lag exceeds maxReplicationLagSeconds, CATCHING_UP.
     * - Otherwise, HOT.
     */
    fun computeReadiness(): StandbyReadiness {
        val hwm = peerCoordinator.highWaterMarkSeq
        if (hwm == 0L) {
            AppLogger.d(TAG, "Readiness=BLOCKED: no replication data (hwm=0)")
            return StandbyReadiness.BLOCKED
        }

        val haConfig = configManager.config.value?.siteHa
        val maxLag = haConfig?.maxReplicationLagSeconds?.toLong() ?: 15L

        // Compute lag from the primary's reported sequence vs our local sequence
        val primaryAgentId = peerCoordinator.leaderAgentId
        val primaryState = primaryAgentId?.let { peerCoordinator.peers[it] }
        val lastHb = primaryState?.lastHeartbeatReceivedAt

        // If we haven't heard from the primary recently, estimate lag from time
        val estimatedLagSeconds = if (lastHb != null) {
            Duration.between(lastHb, Instant.now()).seconds
        } else {
            // No heartbeat data — assume we're behind
            maxLag * 2
        }

        return when {
            estimatedLagSeconds > maxLag * 3 -> {
                AppLogger.d(TAG, "Readiness=BLOCKED: lag=${estimatedLagSeconds}s exceeds ${maxLag * 3}s")
                StandbyReadiness.BLOCKED
            }
            estimatedLagSeconds > maxLag -> {
                AppLogger.d(TAG, "Readiness=CATCHING_UP: lag=${estimatedLagSeconds}s exceeds ${maxLag}s")
                StandbyReadiness.CATCHING_UP
            }
            else -> {
                StandbyReadiness.HOT
            }
        }
    }
}

/**
 * Readiness classification for a standby agent.
 */
enum class StandbyReadiness {
    /** Fully synchronized, safe to promote with minimal/no data loss. */
    HOT,
    /** Replication is active but behind; promotion may lose recent data. */
    CATCHING_UP,
    /** Replication stalled or no data; promotion would cause significant data loss. */
    BLOCKED,
}

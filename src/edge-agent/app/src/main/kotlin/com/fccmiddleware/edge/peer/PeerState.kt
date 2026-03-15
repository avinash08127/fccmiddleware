package com.fccmiddleware.edge.peer

import java.time.Instant

/**
 * Tracks the observed state of a single peer agent in the HA cluster.
 *
 * Updated on heartbeat receipt and suspect evaluation. Thread-safety is
 * provided by the owning [PeerCoordinator] which accesses peer state through
 * a [java.util.concurrent.ConcurrentHashMap].
 */
data class PeerState(
    val agentId: String,
    val deviceClass: String,
    val currentRole: String,
    val leaderEpoch: Long,
    val peerApiBaseUrl: String?,
    val lastHeartbeatReceivedAt: Instant? = null,
    val consecutiveMissedHeartbeats: Int = 0,
    val suspectStatus: SuspectStatus = SuspectStatus.HEALTHY,
    val replicationLagSeconds: Double = 0.0,
    /** Cloud-delivered peer directory version; distinct from leaderEpoch (HA election term). */
    val peerDirectoryVersion: Long = 0L,
)

/**
 * Health classification of a peer based on heartbeat liveness checks.
 *
 * State machine:
 *   HEALTHY -> SUSPECTED (missed > threshold / 2)
 *   SUSPECTED -> CONFIRMED_DOWN (missed > threshold)
 *   CONFIRMED_DOWN -> HEALTHY (heartbeat received)
 */
enum class SuspectStatus {
    /** Peer is responding within expected heartbeat intervals. */
    HEALTHY,
    /** Peer has missed several heartbeats but is not yet confirmed down. */
    SUSPECTED,
    /** Peer has exceeded the failure threshold and is considered down. */
    CONFIRMED_DOWN,
}

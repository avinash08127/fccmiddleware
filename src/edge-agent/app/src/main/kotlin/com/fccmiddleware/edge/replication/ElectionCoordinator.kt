package com.fccmiddleware.edge.replication

import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.logging.StructuredFileLogger
import com.fccmiddleware.edge.peer.PeerCoordinator
import com.fccmiddleware.edge.peer.PeerLeadershipClaimRequest
import com.fccmiddleware.edge.peer.PeerHttpClient
import com.fccmiddleware.edge.security.EncryptedPrefsManager

/**
 * ElectionCoordinator — manages leader election for HA failover.
 *
 * Algorithm (mirrors desktop election):
 * 1. Verify primary is suspected (direct health probe to confirm).
 * 2. Check standby readiness (HOT or CATCHING_UP — BLOCKED agents do not run).
 * 3. Propose a new epoch (current epoch + 1).
 * 4. Send leadership claim to all known peers.
 * 5. If a majority (including self) accept, transition to PRIMARY.
 * 6. If rejected, remain in current role and retry on next evaluation.
 *
 * The epoch is persisted in EncryptedPrefsManager so it survives restarts.
 * Election is single-threaded — only one election attempt runs at a time.
 */
class ElectionCoordinator(
    private val peerCoordinator: PeerCoordinator,
    private val peerHttpClient: PeerHttpClient,
    private val configManager: ConfigManager,
    private val encryptedPrefsManager: EncryptedPrefsManager,
    private val standbyReadinessGate: StandbyReadinessGate,
    private val fileLogger: StructuredFileLogger,
) {

    companion object {
        private const val TAG = "ElectionCoordinator"
    }

    @Volatile
    private var electionInProgress = false

    /**
     * Attempt to run a leader election.
     *
     * Preconditions checked by caller (CadenceController):
     * - Current role is STANDBY_HOT or RECOVERING
     * - Primary is suspected
     * - Auto-failover is enabled
     *
     * @return [ElectionResult] indicating whether this agent became PRIMARY.
     */
    fun tryElection(): ElectionResult {
        if (electionInProgress) {
            AppLogger.d(TAG, "Election already in progress — skipping")
            return ElectionResult.AlreadyInProgress
        }

        electionInProgress = true
        try {
            return runElection()
        } finally {
            electionInProgress = false
        }
    }

    private fun runElection(): ElectionResult {
        val localAgentId = encryptedPrefsManager.deviceId
        if (localAgentId == null) {
            AppLogger.e(TAG, "Cannot run election: no local deviceId")
            return ElectionResult.Failed("No local deviceId")
        }

        val localSiteCode = encryptedPrefsManager.siteCode
        if (localSiteCode == null) {
            AppLogger.e(TAG, "Cannot run election: no local siteCode")
            return ElectionResult.Failed("No local siteCode")
        }

        val haConfig = configManager.config.value?.siteHa
        if (haConfig == null || !haConfig.enabled || !haConfig.autoFailoverEnabled) {
            return ElectionResult.Failed("HA or auto-failover not enabled")
        }

        // Step 1: Confirm primary is actually down with a direct probe
        val primaryAgentId = peerCoordinator.leaderAgentId
        if (primaryAgentId != null && primaryAgentId != localAgentId) {
            val probeResult = peerCoordinator.directHealthProbe(primaryAgentId)
            if (probeResult) {
                AppLogger.i(TAG, "Direct probe to primary $primaryAgentId succeeded — aborting election")
                return ElectionResult.PrimaryAlive
            }
            AppLogger.w(TAG, "Direct probe to primary $primaryAgentId failed — proceeding with election")
        }

        // Step 2: Check standby readiness
        val readiness = standbyReadinessGate.computeReadiness()
        if (readiness == StandbyReadiness.BLOCKED) {
            AppLogger.w(TAG, "Standby readiness is BLOCKED — cannot run election")
            return ElectionResult.ReadinessBlocked
        }

        // Step 3: Propose new epoch
        val proposedEpoch = peerCoordinator.leaderEpoch + 1
        val localPriority = haConfig.priority

        AppLogger.i(TAG, "Starting election: proposedEpoch=$proposedEpoch, priority=$localPriority")
        fileLogger.i(TAG, "Election started: proposedEpoch=$proposedEpoch, readiness=$readiness")

        val claimRequest = PeerLeadershipClaimRequest(
            candidateAgentId = localAgentId,
            proposedEpoch = proposedEpoch,
            priority = localPriority,
            siteCode = localSiteCode,
        )

        // Step 4: Send claim to all peers
        var acceptCount = 1 // Self-vote
        var rejectCount = 0
        val totalVoters = peerCoordinator.peers.size + 1 // Including self
        val majorityNeeded = (totalVoters / 2) + 1

        for ((agentId, peerState) in peerCoordinator.peers) {
            val baseUrl = peerState.peerApiBaseUrl ?: continue
            val response = peerHttpClient.claimLeadership(baseUrl, claimRequest)
            if (response != null && response.accepted) {
                acceptCount++
                AppLogger.d(TAG, "Peer $agentId accepted leadership claim")
            } else {
                rejectCount++
                val reason = response?.reason ?: "no response"
                AppLogger.d(TAG, "Peer $agentId rejected leadership claim: $reason")
            }
        }

        // Step 5: Evaluate results
        if (acceptCount >= majorityNeeded) {
            // Election won — transition to PRIMARY
            peerCoordinator.leaderEpoch = proposedEpoch
            peerCoordinator.leaderAgentId = localAgentId
            peerCoordinator.currentRole = "PRIMARY"

            AppLogger.i(
                TAG,
                "Election WON: promoted to PRIMARY at epoch $proposedEpoch " +
                    "(votes: $acceptCount/$totalVoters, needed: $majorityNeeded)",
            )
            fileLogger.i(TAG, "Election won: epoch=$proposedEpoch, votes=$acceptCount/$totalVoters")

            return ElectionResult.Won(proposedEpoch)
        } else {
            AppLogger.w(
                TAG,
                "Election LOST: $acceptCount/$totalVoters votes (needed $majorityNeeded)",
            )
            return ElectionResult.Lost(acceptCount, totalVoters, majorityNeeded)
        }
    }
}

/**
 * Result of an election attempt.
 */
sealed class ElectionResult {
    /** Election won — this agent is now PRIMARY at the given epoch. */
    data class Won(val epoch: Long) : ElectionResult()

    /** Election lost — not enough votes. */
    data class Lost(val votes: Int, val total: Int, val needed: Int) : ElectionResult()

    /** Primary is still alive based on direct probe. */
    data object PrimaryAlive : ElectionResult()

    /** Standby readiness is BLOCKED — cannot participate in election. */
    data object ReadinessBlocked : ElectionResult()

    /** Another election is already in progress. */
    data object AlreadyInProgress : ElectionResult()

    /** Election could not run due to configuration or state error. */
    data class Failed(val reason: String) : ElectionResult()
}

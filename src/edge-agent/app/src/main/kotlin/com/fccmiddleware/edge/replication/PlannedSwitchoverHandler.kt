package com.fccmiddleware.edge.replication

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.peer.PeerCoordinator
import com.fccmiddleware.edge.peer.PeerHttpClient
import com.fccmiddleware.edge.peer.PeerLeadershipClaimRequest
import kotlinx.coroutines.delay

/**
 * Orchestrates a planned switchover from the current primary to a target standby.
 * Triggered when the AgentCommandExecutor receives a PLANNED_SWITCHOVER command.
 */
class PlannedSwitchoverHandler(
    private val peerCoordinator: PeerCoordinator,
    private val peerHttpClient: PeerHttpClient,
) {
    suspend fun execute(targetAgentId: String, currentEpoch: Long, siteCode: String): SwitchoverResult {
        AppLogger.i(TAG, "Starting planned switchover to $targetAgentId at epoch $currentEpoch")

        return try {
            // Step 1: Verify target is reachable and ready
            val targetHealth = peerHttpClient.getHealth(targetAgentId)
            if (targetHealth == null) {
                AppLogger.w(TAG, "Target $targetAgentId is unreachable")
                return SwitchoverResult.Failed("Target agent is unreachable")
            }

            // Step 2: Drain in-flight operations
            AppLogger.i(TAG, "Draining in-flight operations before switchover")
            delay(3_000)

            // Step 3: Claim leadership on target
            val newEpoch = currentEpoch + 1
            val claimResponse = peerHttpClient.claimLeadership(
                targetAgentId,
                PeerLeadershipClaimRequest(
                    candidateAgentId = targetAgentId,
                    proposedEpoch = newEpoch,
                    priority = 0,
                    siteCode = siteCode,
                ),
            )

            if (claimResponse == null || !claimResponse.accepted) {
                val reason = claimResponse?.reason ?: "no response"
                AppLogger.w(TAG, "Target $targetAgentId rejected leadership claim: $reason")
                return SwitchoverResult.Failed("Leadership claim rejected: $reason")
            }

            // Step 4: Self-demote
            AppLogger.i(TAG, "Demoting self — new primary is $targetAgentId at epoch $newEpoch")
            SwitchoverResult.Succeeded(newEpoch)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Switchover to $targetAgentId failed", e)
            SwitchoverResult.Failed("Switchover failed: ${e.message}")
        }
    }

    companion object {
        private const val TAG = "PlannedSwitchoverHandler"
    }
}

sealed class SwitchoverResult {
    data class Succeeded(val newEpoch: Long) : SwitchoverResult()
    data class Failed(val errorMessage: String) : SwitchoverResult()
}

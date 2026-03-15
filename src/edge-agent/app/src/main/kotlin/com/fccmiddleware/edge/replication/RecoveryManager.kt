package com.fccmiddleware.edge.replication

import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.logging.StructuredFileLogger
import com.fccmiddleware.edge.peer.PeerCoordinator
import com.fccmiddleware.edge.security.EncryptedPrefsManager

/**
 * RecoveryManager — handles role transitions after election and during recovery.
 *
 * Responsibilities:
 * - Transition to PRIMARY after winning an election (update coordinator state,
 *   persist epoch, notify peers via next heartbeat).
 * - Handle demotion back to STANDBY_HOT on failback (when allowFailback is enabled
 *   and the original primary comes back online with a higher epoch).
 * - Manage the RECOVERING intermediate state during initial sync.
 *
 * Algorithm (mirrors desktop RecoveryManager):
 * 1. On election won: set role=PRIMARY, persist epoch, begin accepting writes.
 * 2. On new epoch received (higher than local): if allowFailback, step down to STANDBY_HOT.
 * 3. On RECOVERING state: remain in RECOVERING until replication catches up (HOT readiness).
 */
class RecoveryManager(
    private val peerCoordinator: PeerCoordinator,
    private val configManager: ConfigManager,
    private val encryptedPrefsManager: EncryptedPrefsManager,
    private val standbyReadinessGate: StandbyReadinessGate,
    private val fileLogger: StructuredFileLogger,
) {

    companion object {
        private const val TAG = "RecoveryManager"
    }

    /**
     * Finalize promotion to PRIMARY after winning an election.
     *
     * Called by the CadenceController after [ElectionCoordinator.tryElection] returns [ElectionResult.Won].
     * Sets the coordinator role and persists the new epoch.
     */
    fun finalizePromotion(epoch: Long) {
        peerCoordinator.currentRole = "PRIMARY"
        peerCoordinator.leaderEpoch = epoch
        peerCoordinator.leaderAgentId = encryptedPrefsManager.deviceId

        AppLogger.i(TAG, "Finalized promotion to PRIMARY at epoch $epoch")
        fileLogger.i(TAG, "Role transition: -> PRIMARY at epoch $epoch")
    }

    /**
     * Handle a higher epoch discovered from a peer heartbeat or leadership claim.
     *
     * If allowFailback is enabled and the new epoch is from a different agent,
     * step down to STANDBY_HOT. Otherwise, accept the new epoch but remain in
     * current role.
     */
    fun handleEpochAdvancement(newEpoch: Long, newLeaderAgentId: String) {
        val localAgentId = encryptedPrefsManager.deviceId

        if (newEpoch <= peerCoordinator.leaderEpoch) return

        val haConfig = configManager.config.value?.siteHa
        val wasLeader = peerCoordinator.currentRole == "PRIMARY"

        peerCoordinator.leaderEpoch = newEpoch
        peerCoordinator.leaderAgentId = newLeaderAgentId

        if (wasLeader && newLeaderAgentId != localAgentId) {
            if (haConfig?.allowFailback == true) {
                peerCoordinator.currentRole = "STANDBY_HOT"
                AppLogger.i(TAG, "Failback: stepping down from PRIMARY to STANDBY_HOT (new leader=$newLeaderAgentId, epoch=$newEpoch)")
                fileLogger.i(TAG, "Role transition: PRIMARY -> STANDBY_HOT (failback, epoch=$newEpoch)")
            } else {
                // No failback allowed — remain in current state but log the anomaly
                AppLogger.w(
                    TAG,
                    "Higher epoch $newEpoch from $newLeaderAgentId but failback is disabled — " +
                        "remaining PRIMARY (potential split-brain)",
                )
                fileLogger.w(TAG, "Split-brain risk: higher epoch $newEpoch received but failback disabled")
            }
        } else {
            AppLogger.i(TAG, "Epoch advanced to $newEpoch (leader=$newLeaderAgentId)")
        }
    }

    /**
     * Evaluate whether a RECOVERING agent can transition to STANDBY_HOT.
     *
     * Called by the CadenceController when role is RECOVERING. If standby
     * readiness reaches HOT, the agent transitions to STANDBY_HOT.
     */
    fun evaluateRecoveryCompletion() {
        if (peerCoordinator.currentRole != "RECOVERING") return

        val readiness = standbyReadinessGate.computeReadiness()
        if (readiness == StandbyReadiness.HOT) {
            peerCoordinator.currentRole = "STANDBY_HOT"
            AppLogger.i(TAG, "Recovery complete — transitioned to STANDBY_HOT")
            fileLogger.i(TAG, "Role transition: RECOVERING -> STANDBY_HOT")
        } else {
            AppLogger.d(TAG, "Recovery in progress — readiness=$readiness")
        }
    }
}

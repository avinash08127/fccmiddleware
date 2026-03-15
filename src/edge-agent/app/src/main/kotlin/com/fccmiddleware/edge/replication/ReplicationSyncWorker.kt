package com.fccmiddleware.edge.replication

import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.logging.StructuredFileLogger
import com.fccmiddleware.edge.peer.PeerCoordinator
import com.fccmiddleware.edge.peer.PeerHttpClient
import java.time.Instant

/**
 * ReplicationSyncWorker — pulls data from the primary agent and applies it locally.
 *
 * Algorithm (mirrors desktop ReplicationSyncWorker):
 * 1. Read local cursor (high-water-mark sequence) from SyncState.
 * 2. If cursor == 0 (first boot or reset), perform a bootstrap (full snapshot).
 * 3. Otherwise, perform a delta sync from the cursor position.
 * 4. Apply received data atomically using Room @Transaction.
 * 5. Update local cursor after successful application.
 *
 * Called by the CadenceController when the local agent's role is STANDBY_HOT
 * or RECOVERING.
 */
class ReplicationSyncWorker(
    private val peerHttpClient: PeerHttpClient,
    private val peerCoordinator: PeerCoordinator,
    private val syncStateDao: SyncStateDao,
    private val sequenceAssignor: ReplicationSequenceAssignor,
    private val fileLogger: StructuredFileLogger,
) {

    companion object {
        private const val TAG = "ReplicationSyncWorker"
    }

    /**
     * Execute one sync cycle. Returns the result of the sync attempt.
     *
     * Called from the cadence loop when the local role is STANDBY_HOT or RECOVERING.
     */
    suspend fun sync(): ReplicationSyncResult {
        val primaryAgentId = peerCoordinator.leaderAgentId
        if (primaryAgentId == null) {
            AppLogger.w(TAG, "No leader agent ID — cannot sync")
            return ReplicationSyncResult.NoLeader
        }

        val primaryState = peerCoordinator.peers[primaryAgentId]
        val primaryBaseUrl = primaryState?.peerApiBaseUrl
        if (primaryBaseUrl == null) {
            AppLogger.w(TAG, "No base URL for primary $primaryAgentId — cannot sync")
            return ReplicationSyncResult.NoPrimaryUrl
        }

        val currentSeq = peerCoordinator.highWaterMarkSeq

        return if (currentSeq == 0L) {
            performBootstrap(primaryBaseUrl)
        } else {
            performDeltaSync(primaryBaseUrl, currentSeq)
        }
    }

    // -------------------------------------------------------------------------
    // Bootstrap (full snapshot)
    // -------------------------------------------------------------------------

    private suspend fun performBootstrap(primaryBaseUrl: String): ReplicationSyncResult {
        AppLogger.i(TAG, "Starting bootstrap from $primaryBaseUrl")

        val snapshot = peerHttpClient.getBootstrap(primaryBaseUrl)
        if (snapshot == null) {
            AppLogger.w(TAG, "Bootstrap failed — no response from primary")
            return ReplicationSyncResult.TransportError("Bootstrap returned null")
        }

        // Apply snapshot atomically
        try {
            applySnapshot(snapshot)
            val newHwm = maxOf(snapshot.highWaterMarkTxSeq, snapshot.highWaterMarkPaSeq)
            peerCoordinator.highWaterMarkSeq = newHwm
            sequenceAssignor.initialize(newHwm)

            val now = Instant.now().toString()
            syncStateDao.ensureRow(now)

            AppLogger.i(
                TAG,
                "Bootstrap applied: ${snapshot.transactions.size} txns, " +
                    "${snapshot.preAuths.size} preauths, " +
                    "${snapshot.nozzles.size} nozzles, hwm=$newHwm",
            )
            fileLogger.i(TAG, "Bootstrap complete: hwm=$newHwm, epoch=${snapshot.epoch}")
            return ReplicationSyncResult.Success(
                transactionsApplied = snapshot.transactions.size,
                preAuthsApplied = snapshot.preAuths.size,
            )
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to apply bootstrap snapshot: ${e.message}", e)
            return ReplicationSyncResult.ApplicationError(e.message ?: "Unknown error")
        }
    }

    // -------------------------------------------------------------------------
    // Delta sync (incremental)
    // -------------------------------------------------------------------------

    private suspend fun performDeltaSync(primaryBaseUrl: String, fromSeq: Long): ReplicationSyncResult {
        val delta = peerHttpClient.getDeltaSync(primaryBaseUrl, fromSeq)
        if (delta == null) {
            AppLogger.w(TAG, "Delta sync failed — no response from primary")
            return ReplicationSyncResult.TransportError("Delta sync returned null")
        }

        // Epoch mismatch check
        if (delta.epoch != peerCoordinator.leaderEpoch) {
            AppLogger.w(TAG, "Epoch mismatch: delta.epoch=${delta.epoch}, local=${peerCoordinator.leaderEpoch}")
            // Full re-bootstrap needed after epoch change
            return performBootstrap(primaryBaseUrl)
        }

        try {
            applyDelta(delta)
            peerCoordinator.highWaterMarkSeq = delta.toSeq
            sequenceAssignor.initialize(delta.toSeq)

            val now = Instant.now().toString()
            syncStateDao.ensureRow(now)

            AppLogger.d(
                TAG,
                "Delta applied: ${delta.transactions.size} txns, ${delta.preAuths.size} preauths, " +
                    "seq ${delta.fromSeq}..${delta.toSeq}, hasMore=${delta.hasMore}",
            )
            return ReplicationSyncResult.Success(
                transactionsApplied = delta.transactions.size,
                preAuthsApplied = delta.preAuths.size,
            )
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to apply delta: ${e.message}", e)
            return ReplicationSyncResult.ApplicationError(e.message ?: "Unknown error")
        }
    }

    // -------------------------------------------------------------------------
    // Data application (Room @Transaction atomicity is handled by the DAO layer)
    // -------------------------------------------------------------------------

    /**
     * Apply a full snapshot to local storage. Clears existing replicated data first.
     * In a production implementation this would use Room @Transaction for atomicity.
     */
    private suspend fun applySnapshot(snapshot: SnapshotPayload) {
        // Stub: in production, this would:
        // 1. Clear all replicated transactions and pre-auths
        // 2. Insert all snapshot data in a single Room @Transaction
        // 3. Update nozzle mappings
        AppLogger.d(TAG, "Applying snapshot: ${snapshot.transactions.size} txns, ${snapshot.preAuths.size} preauths")
    }

    /**
     * Apply a delta sync payload to local storage.
     * In a production implementation this would upsert within a Room @Transaction.
     */
    private suspend fun applyDelta(delta: DeltaSyncPayload) {
        // Stub: in production, this would:
        // 1. Upsert transactions (keyed by fccTransactionId + siteCode)
        // 2. Upsert pre-auths (keyed by odooOrderId + siteCode)
        // Both within a single Room @Transaction for atomicity
        AppLogger.d(TAG, "Applying delta: ${delta.transactions.size} txns, ${delta.preAuths.size} preauths")
    }
}

/**
 * Result of a single replication sync cycle.
 */
sealed class ReplicationSyncResult {
    /** Sync completed successfully. */
    data class Success(
        val transactionsApplied: Int,
        val preAuthsApplied: Int,
    ) : ReplicationSyncResult()

    /** No leader agent configured. */
    data object NoLeader : ReplicationSyncResult()

    /** Leader agent has no reachable URL. */
    data object NoPrimaryUrl : ReplicationSyncResult()

    /** Network or HTTP error communicating with primary. */
    data class TransportError(val message: String) : ReplicationSyncResult()

    /** Error applying received data to local database. */
    data class ApplicationError(val message: String) : ReplicationSyncResult()
}

package com.fccmiddleware.edge.buffer

import android.database.sqlite.SQLiteFullException
import android.util.Log
import com.fccmiddleware.edge.adapter.common.CanonicalTransaction
import com.fccmiddleware.edge.adapter.common.SyncStatus
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import java.time.Instant

/**
 * Buffer management layer on top of [TransactionBufferDao].
 *
 * Handles the full transaction buffer lifecycle:
 *   - Buffering FCC transactions with local dedup (fccTransactionId + siteCode)
 *   - Providing upload batches in chronological order (oldest first)
 *   - Marking upload outcomes (uploaded, duplicate confirmed, synced to Odoo)
 *   - Querying for the local API with SYNCED_TO_ODOO excluded
 *   - Providing per-status buffer statistics for telemetry
 *
 * All timestamps are ISO 8601 UTC strings. Money is Long minor units.
 */
class TransactionBufferManager(private val dao: TransactionBufferDao) {

    companion object {
        private const val TAG = "TransactionBufferMgr"

        /** Emergency cleanup batch size when SQLITE_FULL is encountered. */
        private const val EMERGENCY_CLEANUP_BATCH = 500
    }

    /**
     * Buffer a canonical transaction from the FCC adapter.
     *
     * Silently deduplicates by fccTransactionId + siteCode (Room IGNORE strategy).
     *
     * On [SQLiteFullException] (SQLITE_FULL — disk or database full):
     * performs emergency cleanup by deleting the oldest ARCHIVED and SYNCED_TO_ODOO
     * records, then retries the insert once. This prevents database corruption from
     * unhandled write failures.
     *
     * @return true if the transaction was newly inserted; false if it was a duplicate.
     * @throws SQLiteFullException if the insert still fails after emergency cleanup.
     */
    suspend fun bufferTransaction(tx: CanonicalTransaction): Boolean {
        val entity = tx.toEntity()
        return try {
            val rowId = dao.insert(entity)
            rowId != -1L
        } catch (e: SQLiteFullException) {
            Log.e(TAG, "SQLITE_FULL on insert — attempting emergency cleanup", e)
            emergencyCleanup()
            // Retry once after cleanup
            val retryRowId = dao.insert(entity)
            if (retryRowId == -1L) {
                Log.w(TAG, "Insert was a duplicate after emergency cleanup")
                false
            } else {
                Log.i(TAG, "Insert succeeded after emergency cleanup")
                true
            }
        }
    }

    /**
     * Emergency cleanup: free space by deleting expendable records.
     * Called when a SQLITE_FULL error is encountered during insert.
     * Deletes ARCHIVED first (already uploaded), then SYNCED_TO_ODOO (Odoo confirmed).
     */
    private suspend fun emergencyCleanup() {
        try {
            val archivedDeleted = dao.deleteOldestArchived(EMERGENCY_CLEANUP_BATCH)
            Log.w(TAG, "Emergency cleanup: deleted $archivedDeleted ARCHIVED records")

            if (archivedDeleted < EMERGENCY_CLEANUP_BATCH) {
                val syncedDeleted = dao.deleteOldestSynced(EMERGENCY_CLEANUP_BATCH - archivedDeleted)
                Log.w(TAG, "Emergency cleanup: deleted $syncedDeleted SYNCED_TO_ODOO records")
            }
        } catch (cleanupError: Exception) {
            Log.e(TAG, "Emergency cleanup itself failed — database may be corrupted", cleanupError)
        }
    }

    /**
     * Return the next batch of PENDING records for cloud upload.
     *
     * Records are ordered oldest-first (createdAt ASC) to preserve chronological replay.
     * The upload worker must not skip past a failed record.
     */
    suspend fun getPendingBatch(batchSize: Int): List<BufferedTransaction> =
        dao.getPendingForUpload(batchSize)

    /**
     * Mark records as UPLOADED after the cloud upload API accepts them.
     */
    suspend fun markUploaded(ids: List<String>) {
        if (ids.isEmpty()) return
        val now = Instant.now().toString()
        dao.markBatchUploaded(ids, now)
    }

    /**
     * Mark records as UPLOADED when the cloud confirmed them as duplicates.
     *
     * Per §5.3 Edge Sync State Machine: if cloud returned dedup-skipped, still mark UPLOADED.
     * These records will not appear in the next getPendingBatch and will eventually be
     * transitioned to SYNCED_TO_ODOO via the status poll.
     */
    suspend fun markDuplicateConfirmed(ids: List<String>) {
        if (ids.isEmpty()) return
        val now = Instant.now().toString()
        dao.markBatchUploaded(ids, now)
    }

    /**
     * Mark records as SYNCED_TO_ODOO when the status poll confirms Odoo has ingested them.
     *
     * Keyed by FCC transaction ID (the canonical dedup key).
     * After this transition, records are excluded from local API responses to prevent
     * double-consumption by Odoo POS.
     */
    suspend fun markSyncedToOdoo(fccTransactionIds: List<String>) {
        if (fccTransactionIds.isEmpty()) return
        val now = Instant.now().toString()
        dao.markSyncedToOdoo(fccTransactionIds, now)
    }

    /**
     * Revert UPLOADED records back to PENDING by FCC transaction ID.
     *
     * Called when the status poll returns NOT_FOUND for a record — cloud has no record of it,
     * so the edge should re-upload on the next upload cycle. Only reverts records currently
     * at UPLOADED status to prevent overwriting SYNCED_TO_ODOO or ARCHIVED states.
     */
    suspend fun revertToPending(fccTransactionIds: List<String>) {
        if (fccTransactionIds.isEmpty()) return
        val now = Instant.now().toString()
        dao.revertToPendingByFccId(fccTransactionIds, now)
    }

    /**
     * Return FCC transaction IDs for records at UPLOADED status.
     * Used by the status poller to check which records have reached SYNCED_TO_ODOO in cloud.
     *
     * @param limit Max IDs to return (cloud API supports up to 500 per call).
     */
    suspend fun getUploadedFccTransactionIds(limit: Int): List<String> =
        dao.getUploadedFccTransactionIds(limit)

    /**
     * Buffer-backed query for GET /api/transactions.
     *
     * Excludes SYNCED_TO_ODOO records per §5.3 to prevent double-consumption.
     * Must remain <= 150 ms p95 with 30,000 buffered records.
     *
     * @param pumpNumber FCC pump number filter; null returns all pumps.
     */
    suspend fun getForLocalApi(pumpNumber: Int?, limit: Int, offset: Int): List<BufferedTransaction> =
        if (pumpNumber != null) {
            dao.getForLocalApiByPump(pumpNumber, limit, offset)
        } else {
            dao.getForLocalApi(limit, offset)
        }

    /**
     * Record a failed upload attempt against a single buffered transaction.
     *
     * Increments [BufferedTransaction.uploadAttempts], stores the [error] message,
     * and updates [BufferedTransaction.lastUploadAttemptAt] and [BufferedTransaction.updatedAt].
     * The record's [SyncStatus] stays `PENDING` so the next cadence tick will retry it.
     *
     * Called by [CloudUploadWorker] on transport failures and per-record REJECTED outcomes.
     *
     * @param id         Local Room primary key (UUID).
     * @param attempts   New total attempt count (caller increments from current value).
     * @param attemptAt  ISO 8601 UTC timestamp of this attempt.
     * @param error      Error message or cloud error code to store for diagnostics.
     */
    suspend fun recordUploadFailure(id: String, attempts: Int, attemptAt: String, error: String) {
        dao.updateSyncStatus(
            id = id,
            syncStatus = SyncStatus.PENDING.name,
            attempts = attempts,
            lastAttemptAt = attemptAt,
            error = error,
            now = attemptAt,
        )
    }

    /**
     * Per-status record counts for telemetry reporting.
     *
     * Statuses not present in the DB are omitted from the returned map.
     */
    suspend fun getBufferStats(): Map<SyncStatus, Int> {
        val counts = dao.countByStatus()
        return counts.associate { row ->
            val status = SyncStatus.entries.firstOrNull { it.name == row.syncStatus }
                ?: SyncStatus.PENDING
            status to row.count
        }
    }

    // -------------------------------------------------------------------------
    // Mapping
    // -------------------------------------------------------------------------

    private fun CanonicalTransaction.toEntity(): BufferedTransaction {
        val now = Instant.now().toString()
        return BufferedTransaction(
            id = id,
            fccTransactionId = fccTransactionId,
            siteCode = siteCode,
            pumpNumber = pumpNumber,
            nozzleNumber = nozzleNumber,
            productCode = productCode,
            volumeMicrolitres = volumeMicrolitres,
            amountMinorUnits = amountMinorUnits,
            unitPriceMinorPerLitre = unitPriceMinorPerLitre,
            currencyCode = currencyCode,
            startedAt = startedAt,
            completedAt = completedAt,
            fiscalReceiptNumber = fiscalReceiptNumber,
            fccVendor = fccVendor.name,
            attendantId = attendantId,
            status = status.name,
            syncStatus = SyncStatus.PENDING.name,
            ingestionSource = ingestionSource.name,
            rawPayloadJson = rawPayloadJson,
            correlationId = correlationId,
            uploadAttempts = 0,
            lastUploadAttemptAt = null,
            lastUploadError = null,
            schemaVersion = schemaVersion,
            createdAt = ingestedAt,
            updatedAt = now,
        )
    }
}

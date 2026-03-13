package com.fccmiddleware.edge.buffer.dao

import androidx.room.ColumnInfo
import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction

/** Per-status count row returned by [countByStatus]. */
data class StatusCount(
    @ColumnInfo(name = "sync_status") val syncStatus: String,
    @ColumnInfo(name = "count") val count: Int,
)

@Dao
interface TransactionBufferDao {

    /**
     * Insert a transaction. Silently ignores duplicates (dedup key: fcc_transaction_id + site_code).
     * Returns the new rowId, or -1 if the row was ignored.
     */
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insert(transaction: BufferedTransaction): Long

    /**
     * Chronological batch for the upload worker.
     * Returns PENDING records ordered oldest-first so replay preserves chronological order.
     */
    @Query(
        "SELECT * FROM buffered_transactions " +
        "WHERE sync_status = 'PENDING' " +
        "ORDER BY created_at ASC " +
        "LIMIT :limit"
    )
    suspend fun getPendingForUpload(limit: Int): List<BufferedTransaction>

    /**
     * Buffer-backed query for GET /api/transactions (Odoo POS and portal).
     * Excludes SYNCED_TO_ODOO records. Results ordered by completed_at DESC.
     * Uses ix_bt_local_api; must remain <= 150 ms p95 at 30,000 records.
     */
    @Query(
        "SELECT * FROM buffered_transactions " +
        "WHERE sync_status NOT IN ('SYNCED_TO_ODOO') " +
        "ORDER BY completed_at DESC " +
        "LIMIT :limit OFFSET :offset"
    )
    suspend fun getForLocalApi(limit: Int, offset: Int): List<BufferedTransaction>

    /**
     * Pump-filtered variant of [getForLocalApi] for pump-specific queries.
     */
    @Query(
        "SELECT * FROM buffered_transactions " +
        "WHERE sync_status NOT IN ('SYNCED_TO_ODOO') " +
        "AND pump_number = :pumpNumber " +
        "ORDER BY completed_at DESC " +
        "LIMIT :limit OFFSET :offset"
    )
    suspend fun getForLocalApiByPump(pumpNumber: Int, limit: Int, offset: Int): List<BufferedTransaction>

    @Query("SELECT * FROM buffered_transactions WHERE id = :id")
    suspend fun getById(id: String): BufferedTransaction?

    /**
     * Update sync state after an upload attempt.
     * Always updates updated_at to current ISO 8601 UTC string.
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "sync_status = :syncStatus, " +
        "upload_attempts = :attempts, " +
        "last_upload_attempt_at = :lastAttemptAt, " +
        "last_upload_error = :error, " +
        "updated_at = :now " +
        "WHERE id = :id"
    )
    suspend fun updateSyncStatus(
        id: String,
        syncStatus: String,
        attempts: Int,
        lastAttemptAt: String,
        error: String?,
        now: String,
    )

    /**
     * Mark a batch of transactions as SYNCED_TO_ODOO, keyed by FCC transaction ID.
     * Called when the status-poll response confirms Odoo has ingested them.
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "sync_status = 'SYNCED_TO_ODOO', " +
        "updated_at = :now " +
        "WHERE fcc_transaction_id IN (:fccTransactionIds)"
    )
    suspend fun markSyncedToOdoo(fccTransactionIds: List<String>, now: String)

    /**
     * M-14: Transition SYNCED_TO_ODOO records older than cutoff to ARCHIVED.
     * This implements the documented lifecycle: SYNCED_TO_ODOO → ARCHIVED → (deleted).
     * Returns the number of records archived.
     */
    @Query(
        "UPDATE buffered_transactions SET sync_status = 'ARCHIVED', updated_at = :now " +
        "WHERE sync_status = 'SYNCED_TO_ODOO' " +
        "AND updated_at < :cutoffDate"
    )
    suspend fun archiveOldSynced(cutoffDate: String, now: String): Int

    /**
     * Retention cleanup: delete ARCHIVED records older than cutoff.
     * Returns the number of rows deleted.
     */
    @Query(
        "DELETE FROM buffered_transactions " +
        "WHERE sync_status = 'ARCHIVED' " +
        "AND updated_at < :cutoffDate"
    )
    suspend fun deleteOldArchived(cutoffDate: String): Int

    /**
     * Legacy retention cleanup: delete SYNCED_TO_ODOO records older than cutoff.
     * Returns the number of rows deleted.
     */
    @Query(
        "DELETE FROM buffered_transactions " +
        "WHERE sync_status = 'SYNCED_TO_ODOO' " +
        "AND updated_at < :cutoffDate"
    )
    suspend fun deleteOldSynced(cutoffDate: String): Int

    /**
     * Batch-mark records as UPLOADED after a successful cloud upload response.
     * Does not touch upload_attempts — that is only updated on failure via [updateSyncStatus].
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "sync_status = 'UPLOADED', " +
        "updated_at = :now " +
        "WHERE id IN (:ids)"
    )
    suspend fun markBatchUploaded(ids: List<String>, now: String)

    /**
     * Revert UPLOADED records back to PENDING by FCC transaction ID.
     * Used when status poll returns NOT_FOUND — cloud has no record, so the edge re-uploads.
     * Only reverts records currently at UPLOADED status to prevent overwriting other states.
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "sync_status = 'PENDING', " +
        "updated_at = :now " +
        "WHERE fcc_transaction_id IN (:fccTransactionIds) " +
        "AND sync_status = 'UPLOADED'"
    )
    suspend fun revertToPendingByFccId(fccTransactionIds: List<String>, now: String)

    /**
     * Per-status counts for telemetry and diagnostics.
     */
    @Query(
        "SELECT sync_status, COUNT(*) AS count " +
        "FROM buffered_transactions " +
        "GROUP BY sync_status"
    )
    suspend fun countByStatus(): List<StatusCount>

    /**
     * Timestamp of the oldest PENDING record. Returns null if no PENDING records exist.
     * Used by telemetry to compute sync lag.
     */
    @Query(
        "SELECT created_at FROM buffered_transactions " +
        "WHERE sync_status = 'PENDING' " +
        "ORDER BY created_at ASC LIMIT 1"
    )
    suspend fun oldestPendingCreatedAt(): String?

    /**
     * Return FCC transaction IDs for records at UPLOADED status.
     * Used by the SYNCED_TO_ODOO status poller to query cloud for confirmed statuses.
     * Limited to [limit] to respect the cloud API's 500-ID-per-call constraint.
     */
    @Query(
        "SELECT fcc_transaction_id FROM buffered_transactions " +
        "WHERE sync_status = 'UPLOADED' " +
        "ORDER BY created_at ASC " +
        "LIMIT :limit"
    )
    suspend fun getUploadedFccTransactionIds(limit: Int): List<String>

    /**
     * Total count of records visible to the local API (excludes SYNCED_TO_ODOO).
     * Used by CadenceController for backlog depth and by the /api/v1/status endpoint.
     */
    @Query(
        "SELECT COUNT(*) FROM buffered_transactions " +
        "WHERE sync_status NOT IN ('SYNCED_TO_ODOO')"
    )
    suspend fun countForLocalApi(): Int

    /**
     * Total record count across all statuses.
     * Used by quota enforcement to decide whether emergency cleanup is needed.
     */
    @Query("SELECT COUNT(*) FROM buffered_transactions")
    suspend fun countAll(): Int

    /**
     * Force-archive the oldest PENDING records beyond [keepCount].
     * Transitions PENDING → ARCHIVED so the buffer does not grow unbounded
     * when the cloud is unreachable for an extended period.
     * Records are ordered by created_at ASC so the oldest are archived first.
     * Returns the number of records archived.
     */
    @Query(
        "UPDATE buffered_transactions SET sync_status = 'ARCHIVED', updated_at = :now " +
        "WHERE id IN (" +
        "  SELECT id FROM buffered_transactions " +
        "  WHERE sync_status = 'PENDING' " +
        "  ORDER BY created_at ASC " +
        "  LIMIT (SELECT MAX(0, COUNT(*) - :keepCount) FROM buffered_transactions WHERE sync_status = 'PENDING')" +
        ")"
    )
    suspend fun archiveOldestPending(keepCount: Int, now: String): Int

    /**
     * Delete the oldest ARCHIVED records to bring total count under quota.
     * Returns the number of rows deleted.
     */
    @Query(
        "DELETE FROM buffered_transactions WHERE id IN (" +
        "  SELECT id FROM buffered_transactions " +
        "  WHERE sync_status = 'ARCHIVED' " +
        "  ORDER BY created_at ASC " +
        "  LIMIT :deleteCount" +
        ")"
    )
    suspend fun deleteOldestArchived(deleteCount: Int): Int

    /**
     * Delete the oldest SYNCED_TO_ODOO records to free space.
     * Returns the number of rows deleted.
     */
    @Query(
        "DELETE FROM buffered_transactions WHERE id IN (" +
        "  SELECT id FROM buffered_transactions " +
        "  WHERE sync_status = 'SYNCED_TO_ODOO' " +
        "  ORDER BY created_at ASC " +
        "  LIMIT :deleteCount" +
        ")"
    )
    suspend fun deleteOldestSynced(deleteCount: Int): Int

    /**
     * Time-filtered variant of [getForLocalApi]: returns records with completed_at >= [since].
     * [since] must be an ISO 8601 UTC string. Uses ix_bt_local_api index for p95 <= 150 ms.
     */
    @Query(
        "SELECT * FROM buffered_transactions " +
        "WHERE sync_status NOT IN ('SYNCED_TO_ODOO') " +
        "AND completed_at >= :since " +
        "ORDER BY completed_at DESC " +
        "LIMIT :limit OFFSET :offset"
    )
    suspend fun getForLocalApiSince(since: String, limit: Int, offset: Int): List<BufferedTransaction>

    /**
     * Pump-filtered + time-filtered variant for pump-specific queries with a [since] cutoff.
     */
    @Query(
        "SELECT * FROM buffered_transactions " +
        "WHERE sync_status NOT IN ('SYNCED_TO_ODOO') " +
        "AND pump_number = :pumpNumber " +
        "AND completed_at >= :since " +
        "ORDER BY completed_at DESC " +
        "LIMIT :limit OFFSET :offset"
    )
    suspend fun getForLocalApiByPumpSince(pumpNumber: Int, since: String, limit: Int, offset: Int): List<BufferedTransaction>

    // ── WebSocket backward-compat queries (Odoo POS cart workflow) ─────────

    /**
     * Unsynced transactions for the WebSocket "latest" mode.
     * Returns records NOT marked as SYNCED_TO_ODOO, with optional filters.
     */
    @Query(
        "SELECT * FROM buffered_transactions " +
        "WHERE sync_status NOT IN ('SYNCED_TO_ODOO', 'ARCHIVED') " +
        "AND (:pumpNumber IS NULL OR pump_number = :pumpNumber) " +
        "AND (:nozzleNumber IS NULL OR nozzle_number = :nozzleNumber) " +
        "AND (:attendant IS NULL OR attendant_id = :attendant) " +
        "AND (:since IS NULL OR created_at >= :since) " +
        "ORDER BY completed_at DESC " +
        "LIMIT 200"
    )
    suspend fun getUnsyncedForWs(
        pumpNumber: Int?,
        nozzleNumber: Int?,
        attendant: String?,
        since: String?,
    ): List<BufferedTransaction>

    /**
     * All transactions for the WebSocket "all" mode.
     */
    @Query(
        "SELECT * FROM buffered_transactions " +
        "ORDER BY completed_at DESC " +
        "LIMIT 500"
    )
    suspend fun getAllForWs(): List<BufferedTransaction>

    /**
     * Update Odoo cart fields on a transaction (WebSocket manager_update / attendant_update).
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "order_uuid = COALESCE(:orderUuid, order_uuid), " +
        "odoo_order_id = COALESCE(:odooOrderId, odoo_order_id), " +
        "payment_id = COALESCE(:paymentId, payment_id), " +
        "updated_at = :now " +
        "WHERE fcc_transaction_id = :transactionId"
    )
    suspend fun updateOdooFields(
        transactionId: String,
        orderUuid: String?,
        odooOrderId: String?,
        paymentId: String?,
        now: String,
    )

    /**
     * Update add_to_cart flag and optional payment_id (WebSocket attendant_update).
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "add_to_cart = :addToCart, " +
        "payment_id = COALESCE(:paymentId, payment_id), " +
        "updated_at = :now " +
        "WHERE fcc_transaction_id = :transactionId"
    )
    suspend fun updateAddToCart(
        transactionId: String,
        addToCart: Boolean,
        paymentId: String?,
        now: String,
    )

    /**
     * Mark a transaction as discarded (WebSocket manager_manual_update).
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "is_discard = 1, " +
        "updated_at = :now " +
        "WHERE fcc_transaction_id = :transactionId"
    )
    suspend fun markDiscarded(transactionId: String, now: String)

    /**
     * Look up a transaction by FCC transaction ID (for WebSocket update broadcasts).
     */
    @Query("SELECT * FROM buffered_transactions WHERE fcc_transaction_id = :fccTransactionId LIMIT 1")
    suspend fun getByFccTransactionId(fccTransactionId: String): BufferedTransaction?

    /**
     * L-12: Cross-adapter dedup — find an existing transaction matching the same physical
     * dispense event (same site, pump, completion time, and amount) but from a different adapter.
     * Returns the existing record's fcc_transaction_id if found, or null.
     */
    @Query(
        "SELECT fcc_transaction_id FROM buffered_transactions " +
        "WHERE site_code = :siteCode " +
        "AND pump_number = :pumpNumber " +
        "AND completed_at = :completedAt " +
        "AND amount_minor_units = :amountMinorUnits " +
        "AND fcc_transaction_id != :fccTransactionId " +
        "LIMIT 1"
    )
    suspend fun findCrossAdapterDuplicate(
        siteCode: String,
        pumpNumber: Int,
        completedAt: String,
        amountMinorUnits: Long,
        fccTransactionId: String,
    ): String?

    /**
     * Update the fiscal receipt number on a buffered transaction (ADV-7.3).
     * Called after successful post-dispense fiscalization via [IFiscalizationService].
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "fiscal_receipt_number = :receiptCode, " +
        "updated_at = :now " +
        "WHERE id = :id"
    )
    suspend fun updateFiscalReceipt(id: String, receiptCode: String, now: String)

    // ── GAP-1: Dead-letter queries ──────────────────────────────────────────

    /**
     * Transition PENDING records that have exhausted their upload retries to DEAD_LETTER.
     * Returns the number of records dead-lettered.
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "sync_status = 'DEAD_LETTER', " +
        "updated_at = :now " +
        "WHERE sync_status = 'PENDING' " +
        "AND upload_attempts >= :maxAttempts"
    )
    suspend fun deadLetterExhaustedPending(maxAttempts: Int, now: String): Int

    /**
     * Retention cleanup: delete DEAD_LETTER records older than cutoff.
     * Returns the number of rows deleted.
     */
    @Query(
        "DELETE FROM buffered_transactions " +
        "WHERE sync_status = 'DEAD_LETTER' " +
        "AND updated_at < :cutoffDate"
    )
    suspend fun deleteOldDeadLettered(cutoffDate: String): Int

    /**
     * Delete the oldest DEAD_LETTER records to free space (quota enforcement).
     * Returns the number of rows deleted.
     */
    @Query(
        "DELETE FROM buffered_transactions WHERE id IN (" +
        "  SELECT id FROM buffered_transactions " +
        "  WHERE sync_status = 'DEAD_LETTER' " +
        "  ORDER BY created_at ASC " +
        "  LIMIT :deleteCount" +
        ")"
    )
    suspend fun deleteOldestDeadLettered(deleteCount: Int): Int

    /**
     * Count of DEAD_LETTER records for telemetry.
     */
    @Query(
        "SELECT COUNT(*) FROM buffered_transactions " +
        "WHERE sync_status = 'DEAD_LETTER'"
    )
    suspend fun countDeadLettered(): Int

    // ── GAP-2: Stale uploaded revert query ──────────────────────────────────

    /**
     * Revert UPLOADED records older than [cutoffDate] back to PENDING for re-upload.
     * Handles the case where cloud accepted the upload but the Odoo sync poll
     * never confirmed it. Re-uploading is safe because the cloud deduplicates
     * by fccTransactionId. Resets upload_attempts to 0 since the original upload succeeded.
     * Returns the number of records reverted.
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "sync_status = 'PENDING', " +
        "upload_attempts = 0, " +
        "updated_at = :now " +
        "WHERE sync_status = 'UPLOADED' " +
        "AND updated_at < :cutoffDate"
    )
    suspend fun revertStaleUploaded(cutoffDate: String, now: String): Int

    // ── Fiscalization retry queries (GAP-7) ──────────────────────────────

    /**
     * Returns transactions that need fiscalization retry.
     * Only returns records where fiscal_status = 'PENDING', attempts < max,
     * and enough time has passed since the last attempt (backoff threshold).
     */
    @Query(
        "SELECT * FROM buffered_transactions " +
        "WHERE fiscal_status = 'PENDING' " +
        "AND fiscal_attempts < :maxAttempts " +
        "AND (last_fiscal_attempt_at IS NULL OR last_fiscal_attempt_at < :backoffThreshold) " +
        "ORDER BY created_at ASC " +
        "LIMIT :limit"
    )
    suspend fun getPendingFiscalization(maxAttempts: Int, backoffThreshold: String, limit: Int): List<BufferedTransaction>

    /**
     * Record a fiscalization failure: increment attempts, set last attempt timestamp.
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "fiscal_attempts = fiscal_attempts + 1, " +
        "last_fiscal_attempt_at = :now, " +
        "updated_at = :now " +
        "WHERE id = :id"
    )
    suspend fun recordFiscalFailure(id: String, now: String)

    /**
     * Mark a transaction as fiscal dead-letter after exceeding max retry attempts.
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "fiscal_status = 'DEAD_LETTER', " +
        "fiscal_attempts = fiscal_attempts + 1, " +
        "last_fiscal_attempt_at = :now, " +
        "updated_at = :now " +
        "WHERE id = :id"
    )
    suspend fun markFiscalDeadLetter(id: String, now: String)

    /**
     * Mark a transaction as fiscal success with receipt code.
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "fiscal_status = 'SUCCESS', " +
        "fiscal_receipt_number = :receiptCode, " +
        "last_fiscal_attempt_at = :now, " +
        "updated_at = :now " +
        "WHERE id = :id"
    )
    suspend fun markFiscalSuccess(id: String, receiptCode: String, now: String)

    /**
     * Set fiscal_status to PENDING for newly buffered transactions that need fiscalization.
     */
    @Query(
        "UPDATE buffered_transactions SET " +
        "fiscal_status = 'PENDING', " +
        "updated_at = :now " +
        "WHERE id = :id AND fiscal_status = 'NONE'"
    )
    suspend fun markFiscalPending(id: String, now: String)

    /**
     * Count of transactions in each fiscal status for telemetry.
     */
    @Query(
        "SELECT fiscal_status AS sync_status, COUNT(*) AS count " +
        "FROM buffered_transactions " +
        "WHERE fiscal_status IN ('PENDING', 'DEAD_LETTER') " +
        "GROUP BY fiscal_status"
    )
    suspend fun countByFiscalStatus(): List<StatusCount>
}

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
     * Retention cleanup: delete SYNCED_TO_ODOO records older than cutoff.
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
}

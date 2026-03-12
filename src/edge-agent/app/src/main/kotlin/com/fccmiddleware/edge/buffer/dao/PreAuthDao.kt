package com.fccmiddleware.edge.buffer.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import com.fccmiddleware.edge.buffer.entity.PreAuthRecord

@Dao
interface PreAuthDao {

    /**
     * Insert a pre-auth record. Silently ignores duplicates (idemp key: odoo_order_id + site_code).
     * Returns the new rowId, or -1 if the row was ignored (idempotent retry).
     */
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insert(record: PreAuthRecord): Long

    /**
     * Idempotency lookup — called before inserting a new pre-auth to detect Odoo retries.
     */
    @Query(
        "SELECT * FROM pre_auth_records " +
        "WHERE odoo_order_id = :odooOrderId AND site_code = :siteCode"
    )
    suspend fun getByOdooOrderId(odooOrderId: String, siteCode: String): PreAuthRecord?

    /**
     * Cloud forward worker: unsynced records ordered oldest-first for chronological forwarding.
     * Legacy rows missing unit price are returned once (while cloudSyncAttempts = 0) so the
     * worker can mark them as incomplete instead of retrying forever with fabricated data.
     */
    @Query(
        "SELECT * FROM pre_auth_records " +
        "WHERE is_cloud_synced = 0 " +
        "AND (unit_price_minor_per_litre IS NOT NULL OR cloud_sync_attempts = 0) " +
        "ORDER BY created_at ASC " +
        "LIMIT :limit"
    )
    suspend fun getUnsynced(limit: Int): List<PreAuthRecord>

    /**
     * Update status and related fields after FCC interaction or state transition.
     */
    @Query(
        "UPDATE pre_auth_records SET " +
        "status = :status, " +
        "fcc_correlation_id = :fccCorrelationId, " +
        "fcc_authorization_code = :fccAuthorizationCode, " +
        "failure_reason = :failureReason, " +
        "authorized_at = :authorizedAt, " +
        "completed_at = :completedAt " +
        "WHERE id = :id"
    )
    suspend fun updateStatus(
        id: String,
        status: String,
        fccCorrelationId: String?,
        fccAuthorizationCode: String?,
        failureReason: String?,
        authorizedAt: String?,
        completedAt: String?,
    )

    /**
     * Mark a pre-auth record as successfully synced to cloud.
     * Increments cloud_sync_attempts and sets is_cloud_synced = 1.
     */
    @Query(
        "UPDATE pre_auth_records SET " +
        "is_cloud_synced = 1, " +
        "cloud_sync_attempts = cloud_sync_attempts + 1, " +
        "last_cloud_sync_attempt_at = :now " +
        "WHERE id = :id"
    )
    suspend fun markCloudSynced(id: String, now: String)

    /**
     * Cloud forward worker: record a failed sync attempt.
     * Increments cloud_sync_attempts and updates last_cloud_sync_attempt_at
     * but does NOT set is_cloud_synced = 1 (record stays eligible for retry).
     */
    @Query(
        "UPDATE pre_auth_records SET " +
        "cloud_sync_attempts = cloud_sync_attempts + 1, " +
        "last_cloud_sync_attempt_at = :now " +
        "WHERE id = :id"
    )
    suspend fun recordCloudSyncFailure(id: String, now: String)

    /**
     * Expiry worker: find active pre-auths at or past their expiry time.
     */
    @Query(
        "SELECT * FROM pre_auth_records " +
        "WHERE status IN ('PENDING', 'AUTHORIZED', 'DISPENSING') " +
        "AND expires_at <= :now"
    )
    suspend fun getExpiring(now: String): List<PreAuthRecord>

    /**
     * Retention cleanup: delete terminal pre-auth records older than cutoff.
     * Terminal states: COMPLETED, CANCELLED, EXPIRED, FAILED.
     * Returns the number of rows deleted.
     */
    @Query(
        "DELETE FROM pre_auth_records " +
        "WHERE status IN ('COMPLETED', 'CANCELLED', 'EXPIRED', 'FAILED') " +
        "AND created_at < :cutoffDate"
    )
    suspend fun deleteTerminal(cutoffDate: String): Int
}

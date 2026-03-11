package com.fccmiddleware.edge.buffer

import android.util.Log
import com.fccmiddleware.edge.buffer.dao.AuditLogDao
import com.fccmiddleware.edge.buffer.dao.PreAuthDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.buffer.entity.AuditLog
import java.time.Instant
import java.time.temporal.ChronoUnit

/**
 * Periodic cleanup of stale buffered data.
 *
 * Invoked by [com.fccmiddleware.edge.runtime.CadenceController] on a configurable
 * interval (default: 24 h, from `buffer.cleanupIntervalHours` in site config).
 *
 * NOT a WorkManager job — runs resident under the foreground service cadence controller
 * per architecture rule 16 (resident runtime owns cadence).
 *
 * Cleanup targets:
 *   - SYNCED_TO_ODOO transactions older than [retentionDays]
 *   - Terminal pre-auth records older than [retentionDays] (COMPLETED/CANCELLED/EXPIRED/FAILED)
 *   - Audit log entries older than [retentionDays]
 *
 * [retentionDays] sourced from `SiteConfig.buffer.retentionDays` (default: 7).
 */
class CleanupWorker(
    private val transactionDao: TransactionBufferDao,
    private val preAuthDao: PreAuthDao,
    private val auditLogDao: AuditLogDao,
) {
    companion object {
        private const val TAG = "CleanupWorker"
        const val DEFAULT_RETENTION_DAYS = 7
    }

    data class CleanupResult(
        val transactionsDeleted: Int,
        val preAuthsDeleted: Int,
        val auditEntriesDeleted: Int,
    )

    /**
     * Run all cleanup passes with the given retention window.
     *
     * Safe to call concurrently — each DAO call is its own atomic SQL DELETE.
     *
     * @param retentionDays Days to retain records before deletion. Default: 7.
     * @return Summary of rows deleted across all three tables.
     */
    suspend fun runCleanup(retentionDays: Int = DEFAULT_RETENTION_DAYS): CleanupResult {
        val cutoff = Instant.now()
            .minus(retentionDays.toLong(), ChronoUnit.DAYS)
            .toString()

        val txDeleted = transactionDao.deleteOldSynced(cutoff)
        val preAuthDeleted = preAuthDao.deleteTerminal(cutoff)
        val auditDeleted = auditLogDao.deleteOlderThan(cutoff)

        Log.d(
            TAG,
            "Cleanup complete: tx=$txDeleted preAuth=$preAuthDeleted audit=$auditDeleted" +
                " (cutoff=$cutoff retentionDays=$retentionDays)"
        )

        auditLogDao.insert(
            AuditLog(
                eventType = "CLEANUP_RUN",
                message = "Deleted tx=$txDeleted preAuth=$preAuthDeleted audit=$auditDeleted" +
                    " (retentionDays=$retentionDays)",
                correlationId = null,
                createdAt = Instant.now().toString(),
            )
        )

        return CleanupResult(
            transactionsDeleted = txDeleted,
            preAuthsDeleted = preAuthDeleted,
            auditEntriesDeleted = auditDeleted,
        )
    }
}

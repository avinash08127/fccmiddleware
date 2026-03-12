package com.fccmiddleware.edge.buffer

import android.content.Context
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.buffer.dao.AuditLogDao
import com.fccmiddleware.edge.buffer.dao.PreAuthDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.buffer.entity.AuditLog
import java.io.File
import java.time.Instant
import java.time.temporal.ChronoUnit

/**
 * Periodic cleanup of stale buffered data with quota enforcement.
 *
 * Invoked by [com.fccmiddleware.edge.runtime.CadenceController] on a configurable
 * interval (default: 24 h, from `buffer.cleanupIntervalHours` in site config).
 *
 * NOT a WorkManager job — runs resident under the foreground service cadence controller
 * per architecture rule 16 (resident runtime owns cadence).
 *
 * ## Cleanup strategy (two passes)
 *
 * **Pass 1 — Retention-based** (runs every cycle):
 *   - SYNCED_TO_ODOO transactions older than [retentionDays]
 *   - Terminal pre-auth records older than [retentionDays] (COMPLETED/CANCELLED/EXPIRED/FAILED)
 *   - Audit log entries older than [retentionDays]
 *
 * **Pass 2 — Quota-based** (runs when total record count exceeds [maxBufferRecords]):
 *   1. Delete oldest ARCHIVED records first
 *   2. Delete oldest SYNCED_TO_ODOO records next
 *   3. Force-archive oldest PENDING records beyond [pendingArchiveThreshold] as last resort
 *   This ensures no single extended cloud outage can exhaust device storage.
 *
 * [retentionDays] sourced from `SiteConfig.buffer.retentionDays` (default: 7).
 */
class CleanupWorker(
    private val transactionDao: TransactionBufferDao,
    private val preAuthDao: PreAuthDao,
    private val auditLogDao: AuditLogDao,
    private val context: Context? = null,
) {
    companion object {
        private const val TAG = "CleanupWorker"
        const val DEFAULT_RETENTION_DAYS = 7

        /** Maximum total buffered_transactions records before quota cleanup triggers. */
        const val DEFAULT_MAX_BUFFER_RECORDS = 50_000

        /**
         * When quota cleanup must archive PENDING records, keep at least this many
         * (the most recent ones) so new transactions are not immediately lost.
         */
        const val DEFAULT_PENDING_KEEP_COUNT = 5_000

        /** Minimum free disk space in bytes before we trigger emergency cleanup (50 MB). */
        const val MIN_FREE_DISK_BYTES = 50L * 1024 * 1024

        /** Name of the Room database file (must match BufferDatabase.create). */
        private const val DB_FILE_NAME = "fcc_buffer.db"
    }

    data class CleanupResult(
        val transactionsArchived: Int = 0,
        val transactionsDeleted: Int,
        val preAuthsDeleted: Int,
        val auditEntriesDeleted: Int,
        val quotaArchivedPending: Int = 0,
        val quotaDeletedArchived: Int = 0,
        val quotaDeletedSynced: Int = 0,
        val diskSpaceLow: Boolean = false,
    )

    /**
     * Run all cleanup passes with the given retention window and quota enforcement.
     *
     * Safe to call concurrently — each DAO call is its own atomic SQL operation.
     *
     * @param retentionDays Days to retain records before deletion. Default: 7.
     * @param maxBufferRecords Maximum total records in buffered_transactions. Default: 50,000.
     * @param pendingKeepCount Minimum PENDING records to keep during forced archival. Default: 5,000.
     * @return Summary of rows affected across all cleanup operations.
     */
    suspend fun runCleanup(
        retentionDays: Int = DEFAULT_RETENTION_DAYS,
        maxBufferRecords: Int = DEFAULT_MAX_BUFFER_RECORDS,
        pendingKeepCount: Int = DEFAULT_PENDING_KEEP_COUNT,
    ): CleanupResult {
        // Pass 1: retention-based cleanup
        //   M-14: Follow the documented lifecycle SYNCED_TO_ODOO → ARCHIVED → (deleted).
        //   First archive old SYNCED_TO_ODOO records, then delete old ARCHIVED records.
        val cutoff = Instant.now()
            .minus(retentionDays.toLong(), ChronoUnit.DAYS)
            .toString()
        val now = Instant.now().toString()

        val txArchived = transactionDao.archiveOldSynced(cutoff, now)
        val txDeleted = transactionDao.deleteOldArchived(cutoff)
        val preAuthDeleted = preAuthDao.deleteTerminal(cutoff)
        val auditDeleted = auditLogDao.deleteOlderThan(cutoff)

        AppLogger.d(
            TAG,
            "Retention cleanup: txArchived=$txArchived txDeleted=$txDeleted preAuth=$preAuthDeleted audit=$auditDeleted" +
                " (cutoff=$cutoff retentionDays=$retentionDays)"
        )

        // Pass 2: quota-based cleanup
        val quotaResult = enforceQuota(maxBufferRecords, pendingKeepCount)

        // Pass 3: disk space check — if free space is critically low, aggressively trim
        val diskLow = isDiskSpaceLow()
        if (diskLow) {
            AppLogger.w(TAG, "Disk space critically low — running emergency quota cleanup")
            // Use a tighter quota (half of normal) to aggressively free space
            enforceQuota(maxBufferRecords / 2, pendingKeepCount)
        }

        val result = CleanupResult(
            transactionsArchived = txArchived,
            transactionsDeleted = txDeleted,
            preAuthsDeleted = preAuthDeleted,
            auditEntriesDeleted = auditDeleted,
            quotaArchivedPending = quotaResult.archivedPending,
            quotaDeletedArchived = quotaResult.deletedArchived,
            quotaDeletedSynced = quotaResult.deletedSynced,
            diskSpaceLow = diskLow,
        )

        auditLogDao.insert(
            AuditLog(
                eventType = "CLEANUP_RUN",
                message = buildAuditMessage(result, retentionDays),
                correlationId = null,
                createdAt = Instant.now().toString(),
            )
        )

        return result
    }

    // -------------------------------------------------------------------------
    // Quota enforcement
    // -------------------------------------------------------------------------

    private data class QuotaCleanupResult(
        val deletedArchived: Int = 0,
        val deletedSynced: Int = 0,
        val archivedPending: Int = 0,
    )

    /**
     * Enforce buffer record count quota. Cleanup priority:
     * 1. Delete ARCHIVED (already uploaded, no longer needed)
     * 2. Delete SYNCED_TO_ODOO (Odoo has confirmed receipt)
     * 3. Force-archive oldest PENDING (last resort — these haven't been uploaded yet)
     */
    private suspend fun enforceQuota(
        maxRecords: Int,
        pendingKeepCount: Int,
    ): QuotaCleanupResult {
        val totalCount = transactionDao.countAll()
        if (totalCount <= maxRecords) {
            return QuotaCleanupResult()
        }

        val excess = totalCount - maxRecords
        AppLogger.w(TAG, "Buffer quota exceeded: $totalCount records (max=$maxRecords), need to free $excess")

        val now = Instant.now().toString()
        var remaining = excess

        // Step 1: delete ARCHIVED records first (safest to remove)
        val deletedArchived = if (remaining > 0) {
            transactionDao.deleteOldestArchived(remaining).also { remaining -= it }
        } else 0

        // Step 2: delete SYNCED_TO_ODOO records (Odoo has already received them)
        val deletedSynced = if (remaining > 0) {
            transactionDao.deleteOldestSynced(remaining).also { remaining -= it }
        } else 0

        // Step 3: force-archive oldest PENDING records, keeping at least pendingKeepCount
        val archivedPending = if (remaining > 0) {
            transactionDao.archiveOldestPending(pendingKeepCount, now)
        } else 0

        if (deletedArchived > 0 || deletedSynced > 0 || archivedPending > 0) {
            AppLogger.w(
                TAG,
                "Quota cleanup: deletedArchived=$deletedArchived deletedSynced=$deletedSynced " +
                    "archivedPending=$archivedPending (excess=$excess)"
            )
        }

        return QuotaCleanupResult(
            deletedArchived = deletedArchived,
            deletedSynced = deletedSynced,
            archivedPending = archivedPending,
        )
    }

    // -------------------------------------------------------------------------
    // Disk space check
    // -------------------------------------------------------------------------

    /**
     * Check available disk space on the data partition.
     * Returns true if free space is below [MIN_FREE_DISK_BYTES].
     * Returns false if context is unavailable (conservative — don't over-delete).
     */
    private fun isDiskSpaceLow(): Boolean {
        val ctx = context ?: return false
        return try {
            val dbFile = ctx.getDatabasePath(DB_FILE_NAME)
            val parentDir = dbFile.parentFile ?: File(ctx.dataDir.path)
            val freeSpace = parentDir.usableSpace
            val isLow = freeSpace < MIN_FREE_DISK_BYTES
            if (isLow) {
                AppLogger.w(TAG, "Low disk space: ${freeSpace / (1024 * 1024)} MB free (min: ${MIN_FREE_DISK_BYTES / (1024 * 1024)} MB)")
            }
            isLow
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to check disk space", e)
            false
        }
    }

    // -------------------------------------------------------------------------
    // Audit formatting
    // -------------------------------------------------------------------------

    private fun buildAuditMessage(result: CleanupResult, retentionDays: Int): String {
        val parts = mutableListOf<String>()
        parts += "Retention: txArchived=${result.transactionsArchived} txDeleted=${result.transactionsDeleted} preAuth=${result.preAuthsDeleted} audit=${result.auditEntriesDeleted} (${retentionDays}d)"
        if (result.quotaDeletedArchived > 0 || result.quotaDeletedSynced > 0 || result.quotaArchivedPending > 0) {
            parts += "Quota: archived=${result.quotaDeletedArchived} synced=${result.quotaDeletedSynced} pendingArchived=${result.quotaArchivedPending}"
        }
        if (result.diskSpaceLow) {
            parts += "DISK_LOW"
        }
        return parts.joinToString("; ")
    }
}

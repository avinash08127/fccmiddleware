package com.fccmiddleware.edge.buffer

import android.content.Context
import android.util.Log
import com.fccmiddleware.edge.buffer.dao.AuditLogDao
import com.fccmiddleware.edge.buffer.entity.AuditLog
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.time.Instant

/**
 * Runs `PRAGMA integrity_check` on startup and recovers from corruption.
 *
 * Recovery procedure when corruption is detected:
 *   1. Copy the corrupt database file to a timestamped backup in cacheDir
 *   2. Delete the original db file and WAL/SHM sidecars
 *   3. Room recreates a fresh empty database on next access
 *   4. The foreground service (caller) must reinitialize after [IntegrityCheckResult.Recovered]
 *      (START_STICKY ensures restart; a fresh Koin scope will open a new DB instance)
 *
 * Inject [pragmaRunner] to override the pragma execution in tests.
 */
open class IntegrityChecker(
    private val db: BufferDatabase,
    private val auditLogDao: AuditLogDao,
    private val context: Context,
) {
    companion object {
        private const val TAG = "IntegrityChecker"
        private const val DB_NAME = "fcc_buffer.db"
        private const val PRAGMA_OK = "ok"
    }

    sealed class IntegrityCheckResult {
        /** Database passed integrity check. */
        object Healthy : IntegrityCheckResult()

        /**
         * Database was corrupt, backup was attempted, and original files were deleted.
         * The caller must reinitialize the DB (restart the foreground service).
         */
        data class Recovered(val backupPath: String) : IntegrityCheckResult()
    }

    /**
     * Run integrity check and recover if corruption is detected.
     *
     * Must be called on [Dispatchers.IO] or wrapped in a coroutine context that tolerates
     * blocking I/O. Returns immediately if the database is healthy.
     */
    suspend fun runCheck(): IntegrityCheckResult = withContext(Dispatchers.IO) {
        val issues = readIntegrityCheck()

        if (issues.size == 1 && issues[0].trim().equals(PRAGMA_OK, ignoreCase = true)) {
            Log.d(TAG, "Database integrity check passed")
            return@withContext IntegrityCheckResult.Healthy
        }

        Log.e(TAG, "Database corruption detected (${issues.size} issues): ${issues.joinToString("; ")}")

        // Best-effort: write audit log before closing the DB (may fail on severely corrupt DBs)
        tryWriteCorruptionAuditLog(issues)

        val backupPath = backupAndDelete()

        IntegrityCheckResult.Recovered(backupPath = backupPath ?: "backup-failed")
    }

    /**
     * Execute `PRAGMA integrity_check` and return the result rows.
     * Returns `["ok"]` for a healthy database.
     *
     * Override in tests to inject a synthetic result without touching real files.
     */
    protected open suspend fun readIntegrityCheck(): List<String> =
        withContext(Dispatchers.IO) {
            val results = mutableListOf<String>()
            try {
                db.openHelper.writableDatabase.query("PRAGMA integrity_check").use { cursor ->
                    while (cursor.moveToNext()) {
                        results.add(cursor.getString(0))
                    }
                }
            } catch (e: Exception) {
                Log.e(TAG, "PRAGMA integrity_check threw: ${e.message}")
                results.add("PRAGMA_EXCEPTION: ${e.message}")
            }
            results
        }

    private suspend fun tryWriteCorruptionAuditLog(issues: List<String>) {
        try {
            auditLogDao.insert(
                AuditLog(
                    eventType = "DB_CORRUPTION_DETECTED",
                    message = "Corruption issues (${issues.size}): ${issues.take(5).joinToString("; ")}",
                    correlationId = null,
                    createdAt = Instant.now().toString(),
                )
            )
        } catch (e: Exception) {
            // Audit log write may fail if the DB is severely corrupt — log to logcat only
            Log.w(TAG, "Could not write corruption audit log: ${e.message}")
        }
    }

    private fun backupAndDelete(): String? {
        return try {
            val dbFile = context.getDatabasePath(DB_NAME)
            if (!dbFile.exists()) {
                null
            } else {
                val timestamp = Instant.now().toString().replace(":", "-")
                val backupFile = File(context.cacheDir, "fcc_buffer_corrupt_$timestamp.db")
                dbFile.copyTo(backupFile, overwrite = true)

                // Close the Room connection before deleting (Room holds file locks)
                db.close()
                dbFile.delete()
                File("${dbFile.path}-wal").delete()
                File("${dbFile.path}-shm").delete()

                Log.w(
                    TAG,
                    "Corrupt database backed up to ${backupFile.absolutePath} and deleted for recreation"
                )
                backupFile.absolutePath
            }
        } catch (e: Exception) {
            Log.e(TAG, "Failed to back up corrupt database", e)
            null
        }
    }
}

package com.fccmiddleware.edge.buffer.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import com.fccmiddleware.edge.buffer.entity.AuditLog

@Dao
interface AuditLogDao {

    /** Append a new audit log entry. */
    @Insert(onConflict = OnConflictStrategy.ABORT)
    suspend fun insert(entry: AuditLog): Long

    /**
     * Return the most recent [limit] audit log entries in reverse-chronological order.
     * Used by the diagnostics screen.
     */
    @Query(
        "SELECT * FROM audit_log " +
        "ORDER BY created_at DESC " +
        "LIMIT :limit"
    )
    suspend fun getRecent(limit: Int): List<AuditLog>

    /**
     * Retention cleanup: delete entries older than the cutoff ISO 8601 UTC string.
     * Returns the number of rows deleted.
     */
    @Query("DELETE FROM audit_log WHERE created_at < :cutoffDate")
    suspend fun deleteOlderThan(cutoffDate: String): Int
}

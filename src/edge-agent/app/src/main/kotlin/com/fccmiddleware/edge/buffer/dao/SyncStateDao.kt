package com.fccmiddleware.edge.buffer.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Transaction
import com.fccmiddleware.edge.buffer.entity.SyncState

@Dao
abstract class SyncStateDao {

    /** Returns the single sync state row (id = 1), or null on first boot. */
    @Query("SELECT * FROM sync_state WHERE id = 1")
    abstract suspend fun get(): SyncState?

    /**
     * Upsert the sync state row (INSERT OR REPLACE with id = 1).
     * Always pass a SyncState with id = 1.
     */
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsert(state: SyncState)

    // -------------------------------------------------------------------------
    // AT-035: Atomic column-level UPDATE queries — eliminate read-modify-write
    // race conditions in CloudUploadWorker's SyncState updates.
    // Each method ensures the row exists (INSERT OR IGNORE) and then updates
    // only the target column, preventing concurrent workers from clobbering
    // each other's fields.
    // -------------------------------------------------------------------------

    @Query("INSERT OR IGNORE INTO sync_state (id, updated_at) VALUES (1, :now)")
    abstract suspend fun ensureRow(now: String)

    @Query("UPDATE sync_state SET last_upload_at = :now, updated_at = :now WHERE id = 1")
    protected abstract suspend fun setUploadAt(now: String)

    @Query("UPDATE sync_state SET last_status_poll_at = :now, updated_at = :now WHERE id = 1")
    protected abstract suspend fun setStatusPollAt(now: String)

    @Query("UPDATE sync_state SET last_upload_attempt_at = :now, updated_at = :now WHERE id = 1")
    protected abstract suspend fun setUploadAttemptAt(now: String)

    /** Atomically ensure the row exists and update [last_upload_at]. */
    @Transaction
    open suspend fun updateUploadAt(now: String) {
        ensureRow(now)
        setUploadAt(now)
    }

    /** Atomically ensure the row exists and update [last_status_poll_at]. */
    @Transaction
    open suspend fun updateStatusPollAt(now: String) {
        ensureRow(now)
        setStatusPollAt(now)
    }

    /** Atomically ensure the row exists and update [last_upload_attempt_at]. */
    @Transaction
    open suspend fun updateUploadAttemptAt(now: String) {
        ensureRow(now)
        setUploadAttemptAt(now)
    }

    /**
     * Atomically increment the telemetry sequence counter and return the new value.
     *
     * Uses a Room @Transaction to ensure the UPDATE + SELECT execute in a single
     * SQLite transaction, preventing duplicate sequence numbers even if the app
     * crashes between the write and read. If no row exists yet, initialises the
     * SyncState row with sequence = 1.
     *
     * @return the new (post-increment) sequence number
     */
    @Transaction
    open suspend fun incrementAndGetTelemetrySequence(now: String): Long {
        val existing = get()
        if (existing == null) {
            // First telemetry report — create the row with sequence = 1
            upsert(
                SyncState(
                    lastFccCursor = null,
                    lastUploadAt = null,
                    lastStatusPollAt = null,
                    lastConfigPullAt = null,
                    lastConfigVersion = null,
                    telemetrySequence = 1L,
                    updatedAt = now,
                ),
            )
            return 1L
        }
        val next = existing.telemetrySequence + 1L
        upsert(existing.copy(telemetrySequence = next, updatedAt = now))
        return next
    }
}

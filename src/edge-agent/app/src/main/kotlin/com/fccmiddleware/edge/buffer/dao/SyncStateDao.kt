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

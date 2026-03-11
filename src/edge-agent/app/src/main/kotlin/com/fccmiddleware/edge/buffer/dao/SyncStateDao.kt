package com.fccmiddleware.edge.buffer.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import com.fccmiddleware.edge.buffer.entity.SyncState

@Dao
interface SyncStateDao {

    /** Returns the single sync state row (id = 1), or null on first boot. */
    @Query("SELECT * FROM sync_state WHERE id = 1")
    suspend fun get(): SyncState?

    /**
     * Upsert the sync state row (INSERT OR REPLACE with id = 1).
     * Always pass a SyncState with id = 1.
     */
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(state: SyncState)
}

package com.fccmiddleware.edge.buffer.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import com.fccmiddleware.edge.buffer.entity.AgentConfig

@Dao
interface AgentConfigDao {

    /** Returns the cached site config (id = 1), or null before first config push. */
    @Query("SELECT * FROM agent_config WHERE id = 1")
    suspend fun get(): AgentConfig?

    /**
     * Upsert the site config snapshot (INSERT OR REPLACE with id = 1).
     * Called on each config push from cloud. Always pass an AgentConfig with id = 1.
     */
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(config: AgentConfig)
}

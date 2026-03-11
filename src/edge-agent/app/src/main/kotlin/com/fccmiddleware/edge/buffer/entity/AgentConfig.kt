package com.fccmiddleware.edge.buffer.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * Single-row table (id = 1) caching the full SiteConfig JSON snapshot pushed from cloud.
 *
 * Persisted locally so the agent can bootstrap offline. Applied at runtime
 * by ConfigManager. Upserted via INSERT OR REPLACE with id=1.
 */
@Entity(tableName = "agent_config")
data class AgentConfig(
    /** Always 1 — single-row table */
    @PrimaryKey
    @ColumnInfo(name = "id")
    val id: Int = 1,

    /** Full SiteConfig JSON snapshot as received from cloud */
    @ColumnInfo(name = "config_json")
    val configJson: String,

    /** Config version number from cloud */
    @ColumnInfo(name = "config_version")
    val configVersion: Int,

    /** Schema version of the config JSON format */
    @ColumnInfo(name = "schema_version")
    val schemaVersion: Int,

    /** ISO 8601 UTC; when this config was received from cloud */
    @ColumnInfo(name = "received_at")
    val receivedAt: String,
)

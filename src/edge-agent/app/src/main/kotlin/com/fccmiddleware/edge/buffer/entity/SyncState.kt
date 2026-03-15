package com.fccmiddleware.edge.buffer.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * Single-row table (id = 1) tracking the agent's cloud sync state.
 *
 * Stores the last FCC cursor, upload timestamps, config version, and
 * a monotonic telemetry sequence counter.
 *
 * All timestamps: ISO 8601 UTC TEXT. Upserted via INSERT OR REPLACE with id=1.
 */
@Entity(tableName = "sync_state")
data class SyncState(
    /** Always 1 — single-row table */
    @PrimaryKey
    @ColumnInfo(name = "id")
    val id: Int = 1,

    /** Last successfully acknowledged FCC cursor/offset; null = full catch-up required */
    @ColumnInfo(name = "last_fcc_cursor")
    val lastFccCursor: String? = null,

    /** ISO 8601 UTC; null until first successful upload */
    @ColumnInfo(name = "last_upload_at")
    val lastUploadAt: String? = null,

    /** AF-035: ISO 8601 UTC; null until first upload attempt (successful or failed) */
    @ColumnInfo(name = "last_upload_attempt_at")
    val lastUploadAttemptAt: String? = null,

    /** ISO 8601 UTC; null until first status poll */
    @ColumnInfo(name = "last_status_poll_at")
    val lastStatusPollAt: String? = null,

    /** ISO 8601 UTC; null until first config pull */
    @ColumnInfo(name = "last_config_pull_at")
    val lastConfigPullAt: String? = null,

    /** Config version last applied; null until first config pull */
    @ColumnInfo(name = "last_config_version")
    val lastConfigVersion: Int? = null,

    /** Monotonic counter incremented on each telemetry report */
    @ColumnInfo(name = "telemetry_sequence")
    val telemetrySequence: Long = 0L,

    /** Cloud's peer directory version — tracked so agents detect peer list staleness across restarts. */
    @ColumnInfo(name = "peer_directory_version", defaultValue = "0")
    val peerDirectoryVersion: Long = 0L,

    /** ISO 8601 UTC */
    @ColumnInfo(name = "updated_at")
    val updatedAt: String,
)

package com.fccmiddleware.edge.buffer.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

/**
 * Local diagnostic audit trail for the Edge Agent.
 *
 * Append-only. Used by the diagnostics screen and local troubleshooting.
 * Trimmed by CleanupWorker per retentionDays from SiteConfig.
 * id is AUTOINCREMENT (SQLite INTEGER PRIMARY KEY).
 */
@Entity(
    tableName = "audit_log",
    indices = [
        // Diagnostics screen time filter
        Index(value = ["created_at"], name = "ix_al_time"),
    ]
)
data class AuditLog(
    /** Auto-increment PK — set to 0 or leave default for Room to assign */
    @PrimaryKey(autoGenerate = true)
    @ColumnInfo(name = "id")
    val id: Long = 0,

    @ColumnInfo(name = "event_type")
    val eventType: String,

    @ColumnInfo(name = "message")
    val message: String,

    @ColumnInfo(name = "correlation_id")
    val correlationId: String?,

    /** ISO 8601 UTC */
    @ColumnInfo(name = "created_at")
    val createdAt: String,
)

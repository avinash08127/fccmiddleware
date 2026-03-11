package com.fccmiddleware.agent.buffer

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

@Entity(
    tableName = "buffered_transactions",
    indices = [
        Index(value = ["fcc_transaction_id", "site_code"], unique = true),
        Index(value = ["sync_status"]),
        Index(value = ["created_at"]),
    ]
)
data class BufferedTransactionEntity(
    @PrimaryKey
    @ColumnInfo(name = "id")
    val id: String,

    @ColumnInfo(name = "fcc_transaction_id")
    val fccTransactionId: String,

    @ColumnInfo(name = "site_code")
    val siteCode: String,

    @ColumnInfo(name = "sync_status")
    val syncStatus: String,

    @ColumnInfo(name = "raw_payload")
    val rawPayload: String,

    @ColumnInfo(name = "normalized_payload")
    val normalizedPayload: String?,

    @ColumnInfo(name = "created_at")
    val createdAt: String,

    @ColumnInfo(name = "uploaded_at")
    val uploadedAt: String?,

    @ColumnInfo(name = "retry_count")
    val retryCount: Int = 0,
)

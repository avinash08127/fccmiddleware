package com.fccmiddleware.agent.buffer

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

@Entity(
    tableName = "pre_auth_records",
    indices = [
        Index(value = ["status"]),
        Index(value = ["pump_number"]),
    ]
)
data class PreAuthRecordEntity(
    @PrimaryKey
    @ColumnInfo(name = "id")
    val id: String,

    @ColumnInfo(name = "pump_number")
    val pumpNumber: Int,

    @ColumnInfo(name = "nozzle_number")
    val nozzleNumber: Int?,

    @ColumnInfo(name = "authorized_amount")
    val authorizedAmount: Long,

    @ColumnInfo(name = "status")
    val status: String,

    @ColumnInfo(name = "fcc_correlation_id")
    val fccCorrelationId: String?,

    @ColumnInfo(name = "created_at")
    val createdAt: String,

    @ColumnInfo(name = "updated_at")
    val updatedAt: String,
)

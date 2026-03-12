package com.fccmiddleware.edge.buffer.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * Locally cached pump record derived from nozzle mappings in the cloud config.
 *
 * Stores the Odoo ↔ FCC pump number mapping and an optional display name.
 * Replaced in full on each config refresh.
 */
@Entity(tableName = "local_pumps")
data class LocalPump(
    @PrimaryKey
    @ColumnInfo(name = "odoo_pump_number")
    val odooPumpNumber: Int,

    @ColumnInfo(name = "fcc_pump_number")
    val fccPumpNumber: Int,

    @ColumnInfo(name = "display_name")
    val displayName: String = "",
)

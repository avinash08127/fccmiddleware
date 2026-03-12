package com.fccmiddleware.edge.buffer.entity

import androidx.room.ColumnInfo
import androidx.room.Entity

/**
 * Locally cached nozzle record from the cloud config.
 *
 * Composite PK of (odoo_nozzle_number, odoo_pump_number) — uniquely identifies
 * a nozzle within the site. Replaced in full on each config refresh.
 */
@Entity(
    tableName = "local_nozzles",
    primaryKeys = ["odoo_nozzle_number", "odoo_pump_number"],
)
data class LocalNozzle(
    @ColumnInfo(name = "odoo_nozzle_number")
    val odooNozzleNumber: Int,

    @ColumnInfo(name = "odoo_pump_number")
    val odooPumpNumber: Int,

    @ColumnInfo(name = "fcc_nozzle_number")
    val fccNozzleNumber: Int,

    @ColumnInfo(name = "fcc_pump_number")
    val fccPumpNumber: Int,

    @ColumnInfo(name = "product_code")
    val productCode: String,
)

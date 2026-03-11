package com.fccmiddleware.edge.buffer.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

/**
 * Cached Odoo ↔ FCC pump/nozzle mapping, populated from the cloud config push.
 *
 * The pre-auth handler uses this table to translate the Odoo pump/nozzle numbers
 * received from Odoo POS into the FCC pump/nozzle numbers before sending a
 * pre-auth command to the FCC over LAN.
 *
 * Replaced in full on each config push. Booleans as INTEGER (0/1).
 */
@Entity(
    tableName = "nozzles",
    indices = [
        // Primary pre-auth lookup: Odoo pump + nozzle → FCC pump + nozzle + product
        Index(
            value = ["site_code", "odoo_pump_number", "odoo_nozzle_number"],
            unique = true,
            name = "ix_nozzles_odoo_lookup"
        ),
        // Reverse lookup: FCC pump + nozzle → product code for incoming transactions
        Index(
            value = ["site_code", "fcc_pump_number", "fcc_nozzle_number"],
            unique = true,
            name = "ix_nozzles_fcc_lookup"
        ),
    ]
)
data class Nozzle(
    @PrimaryKey
    @ColumnInfo(name = "id")
    val id: String,

    @ColumnInfo(name = "site_code")
    val siteCode: String,

    /** Pump number as Odoo POS sends it */
    @ColumnInfo(name = "odoo_pump_number")
    val odooPumpNumber: Int,

    /** Pump number to send to FCC */
    @ColumnInfo(name = "fcc_pump_number")
    val fccPumpNumber: Int,

    /** Nozzle number as Odoo POS sends it */
    @ColumnInfo(name = "odoo_nozzle_number")
    val odooNozzleNumber: Int,

    /** Nozzle number to send to FCC */
    @ColumnInfo(name = "fcc_nozzle_number")
    val fccNozzleNumber: Int,

    /** Product dispensed by this nozzle (for pre-auth payload) */
    @ColumnInfo(name = "product_code")
    val productCode: String,

    /** Boolean: 1=active, 0=inactive */
    @ColumnInfo(name = "is_active")
    val isActive: Int = 1,

    /** ISO 8601 UTC; when this mapping was last synced from cloud */
    @ColumnInfo(name = "synced_at")
    val syncedAt: String,

    /** ISO 8601 UTC */
    @ColumnInfo(name = "created_at")
    val createdAt: String,

    /** ISO 8601 UTC */
    @ColumnInfo(name = "updated_at")
    val updatedAt: String,
)

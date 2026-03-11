package com.fccmiddleware.edge.buffer.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Transaction
import com.fccmiddleware.edge.buffer.entity.Nozzle

@Dao
abstract class NozzleDao {

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun insertAll(nozzles: List<Nozzle>)

    @Query("DELETE FROM nozzles WHERE site_code = :siteCode")
    abstract suspend fun deleteBySiteCode(siteCode: String)

    /**
     * Atomically replace the full nozzle set for the site.
     * Called on each config push from cloud.
     */
    @Transaction
    open suspend fun replaceAll(siteCode: String, nozzles: List<Nozzle>) {
        deleteBySiteCode(siteCode)
        insertAll(nozzles)
    }

    /**
     * Pre-auth translation: given Odoo pump + nozzle numbers, return the FCC mapping.
     * Called on every POST /api/preauth — must be fast (hits ix_nozzles_odoo_lookup).
     */
    @Query(
        "SELECT * FROM nozzles " +
        "WHERE site_code = :siteCode " +
        "AND odoo_pump_number = :odooPumpNumber " +
        "AND odoo_nozzle_number = :odooNozzleNumber " +
        "AND is_active = 1"
    )
    abstract suspend fun resolveForPreAuth(
        siteCode: String,
        odooPumpNumber: Int,
        odooNozzleNumber: Int,
    ): Nozzle?

    /**
     * Reverse lookup: given FCC pump + nozzle numbers, return the nozzle record.
     * Used when normalising incoming FCC transactions to populate product_code.
     */
    @Query(
        "SELECT * FROM nozzles " +
        "WHERE site_code = :siteCode " +
        "AND fcc_pump_number = :fccPumpNumber " +
        "AND fcc_nozzle_number = :fccNozzleNumber " +
        "AND is_active = 1"
    )
    abstract suspend fun resolveByFcc(
        siteCode: String,
        fccPumpNumber: Int,
        fccNozzleNumber: Int,
    ): Nozzle?

    /**
     * Return all active nozzles for a site — used by diagnostics / health screen.
     */
    @Query("SELECT * FROM nozzles WHERE site_code = :siteCode AND is_active = 1")
    abstract suspend fun getAll(siteCode: String): List<Nozzle>
}

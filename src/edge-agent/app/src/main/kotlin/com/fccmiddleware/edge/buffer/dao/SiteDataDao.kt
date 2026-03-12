package com.fccmiddleware.edge.buffer.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Transaction
import com.fccmiddleware.edge.buffer.entity.LocalNozzle
import com.fccmiddleware.edge.buffer.entity.LocalProduct
import com.fccmiddleware.edge.buffer.entity.LocalPump
import com.fccmiddleware.edge.buffer.entity.SiteInfo

@Dao
abstract class SiteDataDao {

    // ── SiteInfo ────────────────────────────────────────────────

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun insertSiteInfo(siteInfo: SiteInfo)

    @Query("SELECT * FROM site_info LIMIT 1")
    abstract suspend fun getSiteInfo(): SiteInfo?

    @Query("DELETE FROM site_info")
    abstract suspend fun deleteSiteInfo()

    // ── Products ────────────────────────────────────────────────

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun insertProducts(products: List<LocalProduct>)

    @Query("SELECT * FROM local_products")
    abstract suspend fun getAllProducts(): List<LocalProduct>

    @Query("SELECT * FROM local_products WHERE active = 1")
    abstract suspend fun getActiveProducts(): List<LocalProduct>

    @Query("DELETE FROM local_products")
    abstract suspend fun deleteAllProducts()

    /** Atomically replace all product mappings. */
    @Transaction
    open suspend fun replaceAllProducts(products: List<LocalProduct>) {
        deleteAllProducts()
        insertProducts(products)
    }

    // ── Pumps ───────────────────────────────────────────────────

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun insertPumps(pumps: List<LocalPump>)

    @Query("SELECT * FROM local_pumps")
    abstract suspend fun getAllPumps(): List<LocalPump>

    @Query("DELETE FROM local_pumps")
    abstract suspend fun deleteAllPumps()

    /** Atomically replace all pump records. */
    @Transaction
    open suspend fun replaceAllPumps(pumps: List<LocalPump>) {
        deleteAllPumps()
        insertPumps(pumps)
    }

    // ── Nozzles ─────────────────────────────────────────────────

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun insertNozzles(nozzles: List<LocalNozzle>)

    @Query("SELECT * FROM local_nozzles WHERE odoo_pump_number = :odooPumpNumber")
    abstract suspend fun getNozzlesForPump(odooPumpNumber: Int): List<LocalNozzle>

    @Query("SELECT * FROM local_nozzles")
    abstract suspend fun getAllNozzles(): List<LocalNozzle>

    @Query("DELETE FROM local_nozzles")
    abstract suspend fun deleteAllNozzles()

    /** Atomically replace all nozzle records. */
    @Transaction
    open suspend fun replaceAllNozzles(nozzles: List<LocalNozzle>) {
        deleteAllNozzles()
        insertNozzles(nozzles)
    }

    // ── Bulk operations ─────────────────────────────────────────

    /** Delete all site data tables (used before full re-sync). */
    @Transaction
    open suspend fun deleteAllSiteData() {
        deleteSiteInfo()
        deleteAllProducts()
        deleteAllPumps()
        deleteAllNozzles()
    }

    /**
     * Atomically replace all site master data in one transaction.
     * Called after a successful config fetch from cloud.
     */
    @Transaction
    open suspend fun replaceAllSiteData(
        siteInfo: SiteInfo,
        products: List<LocalProduct>,
        pumps: List<LocalPump>,
        nozzles: List<LocalNozzle>,
    ) {
        deleteSiteInfo()
        deleteAllProducts()
        deleteAllPumps()
        deleteAllNozzles()
        insertSiteInfo(siteInfo)
        insertProducts(products)
        insertPumps(pumps)
        insertNozzles(nozzles)
    }
}

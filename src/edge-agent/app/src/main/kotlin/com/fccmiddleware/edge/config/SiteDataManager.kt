package com.fccmiddleware.edge.config

import com.fccmiddleware.edge.buffer.dao.SiteDataDao
import com.fccmiddleware.edge.buffer.entity.LocalNozzle
import com.fccmiddleware.edge.buffer.entity.LocalProduct
import com.fccmiddleware.edge.buffer.entity.LocalPump
import com.fccmiddleware.edge.buffer.entity.SiteInfo
import com.fccmiddleware.edge.logging.AppLogger
import java.time.Instant

/**
 * Extracts site equipment data from [EdgeAgentConfigDto] and persists it
 * to Room via [SiteDataDao].
 *
 * Called after registration (first config received) and after each
 * successful config poll that yields a new config version.
 */
class SiteDataManager(
    private val siteDataDao: SiteDataDao,
) {
    companion object {
        private const val TAG = "SiteDataManager"
    }

    /**
     * AP-039: In-memory hash of last synced mapping data.
     * Skips the full DELETE+INSERT cycle when only non-mapping config fields changed
     * (e.g., sync intervals, cloud base URL). Resets on process restart — first
     * config fetch after restart always replaces (acceptable, same as before).
     */
    @Volatile
    private var lastMappingHash: Int = 0

    /**
     * Extract products, pumps, nozzles, and site info from [config] and
     * atomically replace all site data tables.
     *
     * AP-039: Computes a deterministic hash of the mapping section. If the hash
     * matches the last sync, only SiteInfo is updated (non-mapping fields like
     * timezone, operating model, etc.) and the full table replacement is skipped.
     */
    suspend fun syncFromConfig(config: EdgeAgentConfigDto) {
        val now = Instant.now().toString()

        val siteInfo = SiteInfo(
            siteCode = config.identity.siteCode,
            siteName = config.identity.siteName,
            legalEntityCode = config.identity.legalEntityCode,
            timezone = config.identity.timezone,
            currencyCode = config.identity.currencyCode,
            operatingModel = config.site.operatingModel,
            fccVendor = config.fcc.vendor,
            fccModel = config.fcc.model,
            ingestionMode = config.fcc.ingestionMode,
            syncedAt = now,
        )

        // AP-039: Hash mapping data to detect changes
        val mappingHash = computeMappingHash(config)
        if (mappingHash == lastMappingHash && lastMappingHash != 0) {
            // Mappings unchanged — update SiteInfo only (non-mapping fields may have changed)
            siteDataDao.insertSiteInfo(siteInfo)
            AppLogger.d(TAG, "AP-039: Mapping data unchanged, site info updated only")
            return
        }

        val products = config.mappings.products.map { p ->
            LocalProduct(
                fccProductCode = p.fccProductCode,
                canonicalProductCode = p.canonicalProductCode,
                displayName = p.displayName,
                active = if (p.active) 1 else 0,
            )
        }

        // Derive unique pumps from nozzle mappings
        val pumps = config.mappings.nozzles
            .map { n -> n.odooPumpNumber to n.fccPumpNumber }
            .distinctBy { it.first }
            .map { (odooPump, fccPump) ->
                LocalPump(
                    odooPumpNumber = odooPump,
                    fccPumpNumber = fccPump,
                )
            }

        val nozzles = config.mappings.nozzles.map { n ->
            LocalNozzle(
                odooNozzleNumber = n.odooNozzleNumber,
                odooPumpNumber = n.odooPumpNumber,
                fccNozzleNumber = n.fccNozzleNumber,
                fccPumpNumber = n.fccPumpNumber,
                productCode = n.productCode,
            )
        }

        siteDataDao.replaceAllSiteData(siteInfo, products, pumps, nozzles)
        lastMappingHash = mappingHash

        AppLogger.i(
            TAG,
            "Site data synced: ${products.size} products, ${pumps.size} pumps, ${nozzles.size} nozzles",
        )
    }

    /**
     * AP-039: Deterministic hash of product and nozzle mapping data.
     * Sorted to ensure order-independence.
     */
    private fun computeMappingHash(config: EdgeAgentConfigDto): Int {
        var hash = 17
        for (p in config.mappings.products.sortedBy { it.fccProductCode }) {
            hash = 31 * hash + p.fccProductCode.hashCode()
            hash = 31 * hash + p.canonicalProductCode.hashCode()
            hash = 31 * hash + p.displayName.hashCode()
            hash = 31 * hash + p.active.hashCode()
        }
        for (n in config.mappings.nozzles.sortedWith(compareBy({ it.odooPumpNumber }, { it.odooNozzleNumber }))) {
            hash = 31 * hash + n.odooNozzleNumber
            hash = 31 * hash + n.odooPumpNumber
            hash = 31 * hash + n.fccNozzleNumber
            hash = 31 * hash + n.fccPumpNumber
            hash = 31 * hash + n.productCode.hashCode()
        }
        return hash
    }
}

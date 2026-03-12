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
     * Extract products, pumps, nozzles, and site info from [config] and
     * atomically replace all site data tables.
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

        AppLogger.i(
            TAG,
            "Site data synced: ${products.size} products, ${pumps.size} pumps, ${nozzles.size} nozzles",
        )
    }
}

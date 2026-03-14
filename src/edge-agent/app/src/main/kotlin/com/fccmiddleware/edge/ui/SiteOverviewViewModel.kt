package com.fccmiddleware.edge.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.adapter.common.PumpState
import com.fccmiddleware.edge.adapter.common.PumpStatus
import com.fccmiddleware.edge.adapter.common.PumpStatusCapability
import com.fccmiddleware.edge.buffer.dao.SiteDataDao
import com.fccmiddleware.edge.buffer.entity.LocalNozzle
import com.fccmiddleware.edge.buffer.entity.LocalProduct
import com.fccmiddleware.edge.buffer.entity.LocalPump
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.runtime.FccRuntimeState
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.time.Instant

class SiteOverviewViewModel(
    private val connectivityManager: ConnectivityManager,
    private val siteDataDao: SiteDataDao,
    private val fccRuntimeState: FccRuntimeState,
) : ViewModel() {

    data class SiteOverviewSnapshot(
        val siteName: String?,
        val siteCode: String?,
        val fccVendor: String?,
        val connectivityState: ConnectivityState,
        val pumpCards: List<PumpCardData>,
        val pumpStatusCapability: PumpStatusCapability?,
        val lastRefreshedAt: String,
    )

    data class PumpCardData(
        val odooPumpNumber: Int,
        val fccPumpNumber: Int,
        val displayName: String,
        val state: PumpState?,
        val nozzles: List<NozzleData>,
        val currentVolumeLitres: String?,
        val currentAmount: String?,
        val unitPrice: String?,
        val currencyCode: String?,
    )

    data class NozzleData(
        val odooNozzleNumber: Int,
        val fccNozzleNumber: Int,
        val productCode: String,
        val productDisplayName: String,
        val isActiveOnPump: Boolean,
    )

    private val _snapshot = MutableStateFlow<SiteOverviewSnapshot?>(null)
    val snapshot: StateFlow<SiteOverviewSnapshot?> = _snapshot.asStateFlow()

    private var autoRefreshJob: kotlinx.coroutines.Job? = null
    private var cachedPumpStatuses: List<PumpStatus> = emptyList()

    fun startAutoRefresh() {
        if (autoRefreshJob?.isActive == true) return
        autoRefreshJob = viewModelScope.launch {
            while (isActive) {
                refresh()
                delay(REFRESH_INTERVAL_MS)
            }
        }
    }

    fun stopAutoRefresh() {
        autoRefreshJob?.cancel()
        autoRefreshJob = null
    }

    private suspend fun refresh() {
        val connState = connectivityManager.state.value

        val data = withContext(Dispatchers.IO) {
            val siteInfo = try { siteDataDao.getSiteInfo() } catch (_: Exception) { null }
            val pumps = try { siteDataDao.getAllPumps() } catch (_: Exception) { emptyList() }
            val nozzles = try { siteDataDao.getAllNozzles() } catch (_: Exception) { emptyList() }
            val products = try { siteDataDao.getAllProducts() } catch (_: Exception) { emptyList() }

            // Fetch live pump status if adapter supports it
            val adapter = fccRuntimeState.adapter
            val capability = adapter?.pumpStatusCapability
            val liveStatuses = if (adapter != null &&
                (capability == PumpStatusCapability.LIVE || capability == PumpStatusCapability.SYNTHESIZED)
            ) {
                val result = try {
                    withTimeoutOrNull(1_000L) { adapter.getPumpStatus() }
                } catch (_: Exception) { null }
                if (result != null) {
                    cachedPumpStatuses = result
                    result
                } else {
                    cachedPumpStatuses
                }
            } else {
                emptyList()
            }

            buildSnapshot(siteInfo, pumps, nozzles, products, liveStatuses, connState, capability)
        }

        _snapshot.value = data
    }

    private fun buildSnapshot(
        siteInfo: com.fccmiddleware.edge.buffer.entity.SiteInfo?,
        pumps: List<LocalPump>,
        nozzles: List<LocalNozzle>,
        products: List<LocalProduct>,
        liveStatuses: List<PumpStatus>,
        connState: ConnectivityState,
        capability: PumpStatusCapability?,
    ): SiteOverviewSnapshot {
        val productMap = products.associateBy { it.fccProductCode }
        val nozzlesByPump = nozzles.groupBy { it.odooPumpNumber }
        val statusByPump = liveStatuses.associateBy { it.pumpNumber }

        val pumpCards = pumps.sortedBy { it.odooPumpNumber }.map { pump ->
            val pumpNozzles = nozzlesByPump[pump.odooPumpNumber] ?: emptyList()
            val status = statusByPump[pump.fccPumpNumber]

            PumpCardData(
                odooPumpNumber = pump.odooPumpNumber,
                fccPumpNumber = pump.fccPumpNumber,
                displayName = pump.displayName,
                state = status?.state,
                currentVolumeLitres = status?.currentVolumeLitres,
                currentAmount = status?.currentAmount,
                unitPrice = status?.unitPrice,
                currencyCode = status?.currencyCode,
                nozzles = pumpNozzles.sortedBy { it.odooNozzleNumber }.map { nozzle ->
                    val product = productMap[nozzle.productCode]
                    NozzleData(
                        odooNozzleNumber = nozzle.odooNozzleNumber,
                        fccNozzleNumber = nozzle.fccNozzleNumber,
                        productCode = nozzle.productCode,
                        productDisplayName = product?.displayName ?: nozzle.productCode,
                        isActiveOnPump = status?.nozzleNumber == nozzle.fccNozzleNumber &&
                            status.state != PumpState.IDLE &&
                            status.state != PumpState.OFFLINE,
                    )
                },
            )
        }

        return SiteOverviewSnapshot(
            siteName = siteInfo?.siteName,
            siteCode = siteInfo?.siteCode,
            fccVendor = buildString {
                siteInfo?.fccVendor?.let { append(it) }
                siteInfo?.fccModel?.let { if (it.isNotEmpty()) append(" / $it") }
            }.ifEmpty { null },
            connectivityState = connState,
            pumpCards = pumpCards,
            pumpStatusCapability = capability,
            lastRefreshedAt = Instant.now().toString().take(19),
        )
    }

    companion object {
        private const val REFRESH_INTERVAL_MS = 3_000L
    }
}

package com.fccmiddleware.edge.adapter.common

/**
 * Optional interface for adapters that support price queries and updates.
 * Ported from legacy ForecourtClient: RequestFcPriceSet(), SendDynamicPriceUpdate().
 */
interface IFccPriceManagement {
    suspend fun getCurrentPrices(): PriceSetSnapshot?
    suspend fun updatePrices(command: PriceUpdateCommand): PriceUpdateResult
}

data class PriceSetSnapshot(
    val priceSetId: String,
    val priceGroupIds: List<String>,
    val grades: List<GradePrice>,
    val observedAtUtc: String,
)

data class GradePrice(
    val gradeId: String,
    val gradeName: String?,
    val priceMinorUnits: Long,
    val currencyCode: String,
)

data class PriceUpdateCommand(
    val updates: List<GradePriceUpdate>,
    val activationTime: String? = null,
)

data class GradePriceUpdate(val gradeId: String, val newPriceMinorUnits: Long)

data class PriceUpdateResult(val success: Boolean, val errorMessage: String? = null)

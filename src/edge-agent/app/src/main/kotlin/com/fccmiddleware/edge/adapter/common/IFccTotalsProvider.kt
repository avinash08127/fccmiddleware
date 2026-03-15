package com.fccmiddleware.edge.adapter.common

/**
 * Optional interface for adapters that support pump totals queries.
 * Ported from legacy FpTotals (EXTC 0x09): TotalVol, TotalMoney per pump.
 */
interface IFccTotalsProvider {
    suspend fun getPumpTotals(): List<PumpTotals>
}

data class PumpTotals(
    val pumpNumber: Int,
    val totalVolumeMicrolitres: Long,
    val totalAmountMinorUnits: Long,
    val currencyCode: String,
    val observedAtUtc: String,
)

package com.fccmiddleware.edge.adapter.doms.protocol

import com.fccmiddleware.edge.adapter.common.PumpTotals
import com.fccmiddleware.edge.adapter.doms.jpl.JplMessage
import com.fccmiddleware.edge.adapter.doms.mapping.DomsCanonicalMapper
import java.time.Instant

/**
 * JPL message builder for pump totals queries.
 * Ported from legacy FpTotals (EXTC 0x09): FpId, TotalVol, TotalMoney.
 */
object DomsTotalsHandler {
    const val TOTALS_REQUEST = "FpTotals_req"
    const val TOTALS_RESPONSE = "FpTotals_resp"

    fun buildTotalsRequest(fpId: Int = 0): JplMessage =
        JplMessage(name = TOTALS_REQUEST, data = mapOf("FpId" to fpId.toString()))

    fun parseTotalsResponse(
        response: JplMessage,
        currencyCode: String,
        pumpNumberOffset: Int,
    ): List<PumpTotals> {
        if (response.name != TOTALS_RESPONSE) return emptyList()
        val data = response.data

        val fpId = data["FpId"]?.toIntOrNull() ?: return emptyList()
        val totalVolCl = data["TotalVol"]?.toLongOrNull() ?: 0L
        val totalMoneyX10 = data["TotalMoney"]?.toLongOrNull() ?: 0L

        return listOf(
            PumpTotals(
                pumpNumber = fpId + pumpNumberOffset,
                totalVolumeMicrolitres = DomsCanonicalMapper.centilitresToMicrolitres(totalVolCl),
                totalAmountMinorUnits = DomsCanonicalMapper.domsAmountToMinorUnits(totalMoneyX10),
                currencyCode = currencyCode,
                observedAtUtc = Instant.now().toString(),
            )
        )
    }
}

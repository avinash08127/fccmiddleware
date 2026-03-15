package com.fccmiddleware.edge.adapter.doms.protocol

import com.fccmiddleware.edge.adapter.common.*
import com.fccmiddleware.edge.adapter.doms.jpl.JplMessage
import java.time.Instant

/**
 * JPL message builders for price management operations.
 * Ported from legacy ForecourtClient: RequestFcPriceSet(), SendDynamicPriceUpdate().
 */
object DomsPriceHandler {
    const val PRICE_SET_REQUEST = "FcPriceSet_req"
    const val PRICE_SET_RESPONSE = "FcPriceSet_resp"
    const val PRICE_UPDATE_REQUEST = "FcPriceUpdate_req"
    const val PRICE_UPDATE_RESPONSE = "FcPriceUpdate_resp"

    private const val RESULT_OK = "0"

    fun buildPriceSetRequest(): JplMessage =
        JplMessage(name = PRICE_SET_REQUEST)

    fun parsePriceSetResponse(response: JplMessage, currencyCode: String): PriceSetSnapshot? {
        if (response.name != PRICE_SET_RESPONSE) return null
        val data = response.data

        val priceSetId = data["PriceSetId"] ?: "unknown"
        val gradeCount = data["GradeCount"]?.toIntOrNull() ?: 0
        val priceGroupIds = data["PriceGroupIds"]?.split(",")?.filter { it.isNotEmpty() } ?: emptyList()

        val grades = (0 until gradeCount).map { i ->
            GradePrice(
                gradeId = data["Grade_${i}_Id"] ?: "%02d".format(i + 1),
                gradeName = data["Grade_${i}_Name"],
                priceMinorUnits = data["Grade_${i}_Price"]?.toLongOrNull() ?: 0L,
                currencyCode = currencyCode,
            )
        }

        return PriceSetSnapshot(priceSetId, priceGroupIds, grades, Instant.now().toString())
    }

    fun buildPriceUpdateRequest(command: PriceUpdateCommand): JplMessage {
        val data = mutableMapOf(
            "PriceSetId" to "01",
            "GradeCount" to command.updates.size.toString(),
        )

        command.updates.forEachIndexed { i, update ->
            data["Grade_${i}_Id"] = update.gradeId
            data["Grade_${i}_Price"] = "%05d".format(update.newPriceMinorUnits)
        }

        if (command.activationTime != null) {
            data["ActivationDate"] = command.activationTime
        }

        return JplMessage(name = PRICE_UPDATE_REQUEST, data = data)
    }

    fun validatePriceUpdateResponse(response: JplMessage): PriceUpdateResult {
        val resultCode = response.data["ResultCode"]
        return if (resultCode == RESULT_OK) {
            PriceUpdateResult(success = true)
        } else {
            PriceUpdateResult(success = false, errorMessage = "Price update failed: ResultCode=${resultCode ?: "missing"}")
        }
    }
}

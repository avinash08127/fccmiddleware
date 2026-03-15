package com.fccmiddleware.edge.adapter.doms.protocol

import com.fccmiddleware.edge.adapter.common.NozzleDetail
import com.fccmiddleware.edge.adapter.common.PumpState
import com.fccmiddleware.edge.adapter.common.PumpStatus
import com.fccmiddleware.edge.adapter.common.PumpStatusSource
import com.fccmiddleware.edge.adapter.common.PumpStatusSupplemental
import com.fccmiddleware.edge.adapter.doms.jpl.JplMessage
import com.fccmiddleware.edge.adapter.doms.model.DomsFpMainState

/**
 * Parses DOMS FpStatus responses into canonical PumpStatus records.
 *
 * JPL messages:
 *   Request:  FpStatus_req  (subCode indicates which pump or all pumps)
 *   Response: FpStatus_resp (data contains pump state fields)
 */
object DomsPumpStatusParser {

    /** JPL message name for pump status request. */
    const val STATUS_REQUEST = "FpStatus_req"

    /** JPL message name for pump status response. */
    const val STATUS_RESPONSE = "FpStatus_resp"

    /**
     * Build a pump status request for all configured pumps.
     *
     * @param fpId Fuelling point ID (0 = all pumps).
     * @return JPL message ready to send.
     */
    fun buildStatusRequest(fpId: Int = 0): JplMessage {
        return JplMessage(
            name = STATUS_REQUEST,
            data = mapOf("FpId" to fpId.toString()),
        )
    }

    /**
     * Parse a pump status response into canonical PumpStatus records.
     *
     * @param response JPL response message.
     * @param siteCode Site identifier for the status record.
     * @param currencyCode ISO 4217 currency code.
     * @param pumpNumberOffset Offset added to raw FCC pump numbers.
     * @param observedAtUtc UTC timestamp for the observation.
     * @return List of parsed pump status records.
     */
    fun parseStatusResponse(
        response: JplMessage,
        siteCode: String,
        currencyCode: String,
        pumpNumberOffset: Int,
        observedAtUtc: String,
    ): List<PumpStatus> {
        if (response.name != STATUS_RESPONSE) {
            return emptyList()
        }

        val data = response.data
        val fpId = data["FpId"]?.toIntOrNull() ?: return emptyList()
        val mainStateCode = data["FpMainState"]?.toIntOrNull() ?: return emptyList()

        val domsFpState = DomsFpMainState.fromCode(mainStateCode)
        val canonicalState = domsFpState?.toCanonicalPumpState() ?: PumpState.UNKNOWN

        val nozzleId = data["NozzleId"]?.toIntOrNull() ?: 1

        // Extract supplemental parameters when present (FpStatus_3 extended data)
        val supplemental = tryParseSupplemental(data)

        return listOf(
            PumpStatus(
                siteCode = siteCode,
                pumpNumber = fpId + pumpNumberOffset,
                nozzleNumber = nozzleId,
                state = canonicalState,
                currencyCode = currencyCode,
                statusSequence = 0,
                observedAtUtc = observedAtUtc,
                source = PumpStatusSource.FCC_LIVE,
                fccStatusCode = mainStateCode.toString(),
                currentVolumeLitres = data["CurrentVolume"],
                currentAmount = data["CurrentAmount"],
                unitPrice = data["UnitPrice"],
                supplemental = supplemental,
            )
        )
    }

    /**
     * Attempt to extract supplemental parameters from JPL data.
     * Returns null if no supplemental fields are present (backwards compatible).
     */
    private fun tryParseSupplemental(data: Map<String, String>): PumpStatusSupplemental? {
        val hasAny = listOf(
            "FpAvailableGrades", "FpAvailableStorageModules", "FpGradeOptionNo",
            "FpFuellingVolumeExt", "FpFuellingMoneyExt", "AttendantAccountId",
            "FpBlockingStatus", "FpOperationModeNo", "PgId",
            "NozzleTagReaderId", "FpAlarmStatus", "NozzleDetailId",
        ).any { data.containsKey(it) }

        if (!hasAny) return null

        return PumpStatusSupplemental(
            availableStorageModules = parseIntList(data, "FpAvailableStorageModules"),
            availableGrades = parseIntList(data, "FpAvailableGrades"),
            gradeOptionNo = data["FpGradeOptionNo"]?.toIntOrNull(),
            fuellingVolumeExtended = data["FpFuellingVolumeExt"]?.toLongOrNull(),
            fuellingMoneyExtended = data["FpFuellingMoneyExt"]?.toLongOrNull(),
            attendantAccountId = data["AttendantAccountId"],
            blockingStatus = data["FpBlockingStatus"],
            nozzleDetail = parseNozzleDetail(data),
            operationModeNo = data["FpOperationModeNo"]?.toIntOrNull(),
            priceGroupId = data["PgId"]?.toIntOrNull(),
            nozzleTagReaderId = data["NozzleTagReaderId"],
            alarmStatus = data["FpAlarmStatus"],
            minPresetValues = parseLongList(data, "FpMinPresetValues"),
        )
    }

    private fun parseIntList(data: Map<String, String>, key: String): List<Int>? {
        val raw = data[key]?.takeIf { it.isNotBlank() } ?: return null
        val result = raw.split(",").mapNotNull { it.trim().toIntOrNull() }
        return result.ifEmpty { null }
    }

    private fun parseLongList(data: Map<String, String>, key: String): List<Long>? {
        val raw = data[key]?.takeIf { it.isNotBlank() } ?: return null
        val result = raw.split(",").mapNotNull { it.trim().toLongOrNull() }
        return result.ifEmpty { null }
    }

    private fun parseNozzleDetail(data: Map<String, String>): NozzleDetail? {
        val id = data["NozzleDetailId"]?.toIntOrNull() ?: return null
        return NozzleDetail(
            id = id,
            asciiCode = data["NozzleDetailAsciiCode"],
            asciiChar = data["NozzleDetailAsciiChar"],
        )
    }
}

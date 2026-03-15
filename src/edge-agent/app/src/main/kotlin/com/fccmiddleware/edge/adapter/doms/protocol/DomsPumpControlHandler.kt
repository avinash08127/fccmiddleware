package com.fccmiddleware.edge.adapter.doms.protocol

import com.fccmiddleware.edge.adapter.common.PumpControlResult
import com.fccmiddleware.edge.adapter.doms.jpl.JplMessage

/**
 * JPL message builders for pump control operations.
 * Ported from legacy ForecourtClient: EmergencyBlock(), UnblockPump(), SoftLock(), Unlock().
 */
object DomsPumpControlHandler {
    const val EMERGENCY_STOP_REQUEST = "FpEmergencyStop_req"
    const val EMERGENCY_STOP_RESPONSE = "FpEmergencyStop_resp"
    const val CANCEL_EMERGENCY_STOP_REQUEST = "FpCancelEmergencyStop_req"
    const val CANCEL_EMERGENCY_STOP_RESPONSE = "FpCancelEmergencyStop_resp"
    const val CLOSE_REQUEST = "FpClose_req"
    const val CLOSE_RESPONSE = "FpClose_resp"
    const val OPEN_REQUEST = "FpOpen_req"
    const val OPEN_RESPONSE = "FpOpen_resp"

    private const val RESULT_OK = "0"

    fun buildEmergencyStopRequest(fpId: Int): JplMessage =
        JplMessage(name = EMERGENCY_STOP_REQUEST, data = mapOf("FpId" to "%02d".format(fpId)))

    fun buildCancelEmergencyStopRequest(fpId: Int): JplMessage =
        JplMessage(name = CANCEL_EMERGENCY_STOP_REQUEST, data = mapOf("FpId" to "%02d".format(fpId)))

    fun buildCloseRequest(fpId: Int): JplMessage =
        JplMessage(name = CLOSE_REQUEST, data = mapOf("FpId" to "%02d".format(fpId)))

    fun buildOpenRequest(fpId: Int): JplMessage =
        JplMessage(name = OPEN_REQUEST, data = mapOf("FpId" to "%02d".format(fpId)))

    fun validateControlResponse(response: JplMessage): PumpControlResult {
        val resultCode = response.data["ResultCode"]
        return if (resultCode == RESULT_OK) {
            PumpControlResult(success = true)
        } else {
            PumpControlResult(success = false, errorMessage = "Pump control failed: ResultCode=${resultCode ?: "missing"}")
        }
    }
}

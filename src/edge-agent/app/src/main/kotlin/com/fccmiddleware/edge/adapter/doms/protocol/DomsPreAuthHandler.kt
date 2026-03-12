package com.fccmiddleware.edge.adapter.doms.protocol

import com.fccmiddleware.edge.adapter.common.PreAuthResultStatus
import com.fccmiddleware.edge.adapter.doms.jpl.JplMessage

/**
 * Handles DOMS pre-authorization (authorize_Fp) operations.
 *
 * Flow: send authorize_Fp_req → receive authorize_Fp_resp → map result code.
 */
object DomsPreAuthHandler {

    /** JPL message name for pre-auth request. */
    const val AUTH_REQUEST = "authorize_Fp_req"

    /** JPL message name for pre-auth response. */
    const val AUTH_RESPONSE = "authorize_Fp_resp"

    // ── Result codes ─────────────────────────────────────────────────────────

    const val AUTH_OK = "0"
    const val AUTH_PUMP_NOT_FOUND = "1"
    const val AUTH_PUMP_NOT_IDLE = "2"
    const val AUTH_ALREADY_AUTHORIZED = "3"
    const val AUTH_LIMIT_EXCEEDED = "4"
    const val AUTH_SYSTEM_ERROR = "99"

    /**
     * Build an authorize_Fp_req message.
     *
     * @param fpId Fuelling point to authorize.
     * @param nozzleId Nozzle to authorize (0 = any nozzle).
     * @param amountMinorUnits Maximum authorized amount in minor currency units.
     * @param currencyCode ISO 4217 currency code.
     * @return JPL message ready to send.
     */
    fun buildAuthRequest(
        fpId: Int,
        nozzleId: Int = 0,
        amountMinorUnits: Long,
        currencyCode: String,
    ): JplMessage {
        // Convert minor units back to DOMS x10 format for the protocol
        val domsAmount = amountMinorUnits / 10L

        return JplMessage(
            name = AUTH_REQUEST,
            data = mapOf(
                "FpId" to fpId.toString(),
                "NozzleId" to nozzleId.toString(),
                "Amount" to domsAmount.toString(),
                "CurrencyCode" to currencyCode,
            ),
        )
    }

    /**
     * Parse an authorize_Fp_resp into a pre-auth result status.
     *
     * @param response JPL response message.
     * @return Pair of (PreAuthResultStatus, authorizationCode/errorMessage).
     */
    fun parseAuthResponse(response: JplMessage): AuthResponseResult {
        if (response.name != AUTH_RESPONSE) {
            return AuthResponseResult(
                status = PreAuthResultStatus.ERROR,
                message = "Unexpected response: ${response.name}",
            )
        }

        val resultCode = response.data["ResultCode"] ?: return AuthResponseResult(
            status = PreAuthResultStatus.ERROR,
            message = "Missing ResultCode in authorize_Fp response",
        )

        return when (resultCode) {
            AUTH_OK -> AuthResponseResult(
                status = PreAuthResultStatus.AUTHORIZED,
                authorizationCode = response.data["AuthCode"],
                expiresAtUtc = response.data["ExpiresAt"],
                correlationId = response.data["CorrelationId"],
            )
            AUTH_PUMP_NOT_FOUND -> AuthResponseResult(
                status = PreAuthResultStatus.DECLINED,
                message = "Pump not found (FpId=${response.data["FpId"]})",
            )
            AUTH_PUMP_NOT_IDLE -> AuthResponseResult(
                status = PreAuthResultStatus.DECLINED,
                message = "Pump not in idle state",
            )
            AUTH_ALREADY_AUTHORIZED -> AuthResponseResult(
                status = PreAuthResultStatus.IN_PROGRESS,
                message = "Pump already has an active authorization",
            )
            AUTH_LIMIT_EXCEEDED -> AuthResponseResult(
                status = PreAuthResultStatus.DECLINED,
                message = "Authorization amount exceeds configured limit",
            )
            else -> AuthResponseResult(
                status = PreAuthResultStatus.ERROR,
                message = "Unknown auth result code: $resultCode (${response.data["ErrorText"] ?: ""})",
            )
        }
    }

    /**
     * Build a deauthorize request (cancel pre-auth).
     */
    fun buildDeauthRequest(fpId: Int): JplMessage {
        return JplMessage(
            name = "deauthorize_Fp_req",
            data = mapOf("FpId" to fpId.toString()),
        )
    }
}

/** Parsed result of a DOMS pre-auth response. */
data class AuthResponseResult(
    val status: PreAuthResultStatus,
    val authorizationCode: String? = null,
    val expiresAtUtc: String? = null,
    val correlationId: String? = null,
    val message: String? = null,
)

package com.fccmiddleware.edge.adapter.doms.protocol

import com.fccmiddleware.edge.adapter.doms.jpl.JplMessage

/**
 * Handles the DOMS FcLogon handshake sequence.
 *
 * FcLogon must complete successfully before any other JPL operations.
 * Sequence: send FcLogon_req → receive FcLogon_resp → validate response code.
 */
object DomsLogonHandler {

    /** JPL message name for FcLogon request. */
    const val LOGON_REQUEST = "FcLogon_req"

    /** JPL message name for FcLogon response. */
    const val LOGON_RESPONSE = "FcLogon_resp"

    /** Response code indicating successful logon. */
    const val LOGON_OK = "0"

    /**
     * Build an FcLogon request message.
     *
     * @param fcAccessCode Access code credential for authentication.
     * @param posVersionId POS version identifier.
     * @param countryCode DOMS country code.
     * @return JPL message ready to send via JplTcpClient.
     */
    fun buildLogonRequest(
        fcAccessCode: String,
        posVersionId: String,
        countryCode: String,
    ): JplMessage {
        return JplMessage(
            name = LOGON_REQUEST,
            data = mapOf(
                "FcAccessCode" to fcAccessCode,
                "PosVersionId" to posVersionId,
                "CountryCode" to countryCode,
            ),
        )
    }

    /**
     * Validate an FcLogon response.
     *
     * @param response The JPL response message.
     * @return true if logon was successful.
     * @throws DomsProtocolException if the response indicates an error.
     */
    fun validateLogonResponse(response: JplMessage): Boolean {
        if (response.name != LOGON_RESPONSE) {
            throw DomsProtocolException(
                "Expected $LOGON_RESPONSE but received ${response.name}"
            )
        }

        val resultCode = response.data["ResultCode"] ?: throw DomsProtocolException(
            "FcLogon response missing ResultCode"
        )

        if (resultCode != LOGON_OK) {
            val errorText = response.data["ErrorText"] ?: "Unknown error"
            throw DomsProtocolException(
                "FcLogon failed with code $resultCode: $errorText"
            )
        }

        return true
    }
}

/**
 * Thrown when a DOMS protocol-level error occurs (bad response, unexpected state, etc.).
 */
class DomsProtocolException(message: String, cause: Throwable? = null) : Exception(message, cause)

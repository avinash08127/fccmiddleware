package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.peer.PeerHttpClient
import com.fccmiddleware.edge.peer.PeerProxyPreAuthRequest
import com.fccmiddleware.edge.peer.PeerProxyPreAuthResponse
import com.fccmiddleware.edge.peer.PeerProxyPumpStatusResponse

/**
 * Proxy client used by standby agents to forward localhost API requests to the primary.
 * Resolves the primary's peer API URL from the config manager's peer directory.
 */
class PrimaryProxyClient(
    private val peerHttpClient: PeerHttpClient,
    private val configManager: ConfigManager,
) {
    /**
     * Proxy a pre-auth request to the primary agent.
     */
    suspend fun proxyPreAuth(
        request: PeerProxyPreAuthRequest,
        correlationId: String?,
    ): ProxyResult<PeerProxyPreAuthResponse> {
        val primaryId = resolvePrimaryAgentId()
            ?: return ProxyResult.PrimaryUnreachable("No primary agent found in peer directory")

        return try {
            val response = peerHttpClient.proxyPreAuth(primaryId, request)
            if (response != null) {
                ProxyResult.Success(response)
            } else {
                ProxyResult.PrimaryUnreachable("Primary did not respond to pre-auth proxy")
            }
        } catch (e: Exception) {
            AppLogger.w(TAG, "Pre-auth proxy to $primaryId failed: ${e.message}")
            ProxyResult.PrimaryUnreachable("Pre-auth proxy failed: ${e.message}")
        }
    }

    /**
     * Proxy a pump status request to the primary agent.
     */
    suspend fun proxyPumpStatus(
        pumpNumber: Int?,
        correlationId: String?,
    ): ProxyResult<PeerProxyPumpStatusResponse> {
        val primaryId = resolvePrimaryAgentId()
            ?: return ProxyResult.PrimaryUnreachable("No primary agent found in peer directory")

        return try {
            val response = peerHttpClient.getPumpStatus(primaryId, pumpNumber)
            if (response != null) {
                ProxyResult.Success(response)
            } else {
                ProxyResult.PrimaryUnreachable("Primary did not respond to pump status proxy")
            }
        } catch (e: Exception) {
            AppLogger.w(TAG, "Pump status proxy to $primaryId failed: ${e.message}")
            ProxyResult.PrimaryUnreachable("Pump status proxy failed: ${e.message}")
        }
    }

    private fun resolvePrimaryAgentId(): String? {
        val siteHa = configManager.config.value?.siteHa ?: return null
        return siteHa.peerDirectory
            .firstOrNull { it.currentRole == "PRIMARY" && it.status == "ACTIVE" }
            ?.agentId
    }

    companion object {
        private const val TAG = "PrimaryProxyClient"
    }
}

sealed class ProxyResult<out T> {
    data class Success<T>(val data: T) : ProxyResult<T>()
    data class PrimaryUnreachable(val message: String) : ProxyResult<Nothing>()
    data class PrimaryRejected(val message: String) : ProxyResult<Nothing>()
}

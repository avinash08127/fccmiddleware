package com.fccmiddleware.edge.peer

import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

/**
 * P2-12: Broadcasts a UDP peer announcement on the station LAN so that other agents
 * on the same site can discover this peer without waiting for cloud config polls.
 *
 * This is Layer 3 of peer discovery — a best-effort LAN broadcast that complements
 * cloud-delivered peer directories (Layer 1) and X-Peer-Directory-Version hints (Layer 2).
 *
 * Broadcast is sent:
 * - On app startup when `siteHa.enabled = true`
 * - After registration completes
 * - When agent role changes (e.g., promoted to PRIMARY)
 *
 * Failures are silently logged — LAN may not support broadcast (e.g., AP isolation).
 */
class LanPeerAnnouncer(
    private val configManager: ConfigManager,
    private val encryptedPrefsManager: EncryptedPrefsManager,
) {

    companion object {
        private const val TAG = "LanPeerAnnouncer"
        const val BROADCAST_PORT = 18586
    }

    @Serializable
    data class PeerAnnouncement(
        val type: String = "PEER_ANNOUNCE",
        val agentId: String,
        val siteCode: String,
        val peerApiHost: String,
        val peerApiPort: Int,
        val peerDirectoryVersion: Long,
    )

    /**
     * Broadcast a PEER_ANNOUNCE datagram to 255.255.255.255:[BROADCAST_PORT].
     * No-op when HA is disabled or required identity fields are unavailable.
     */
    fun broadcast() {
        val config = configManager.config.value ?: return
        val siteHa = config.siteHa
        if (!siteHa.enabled) return

        val agentId = encryptedPrefsManager.deviceId ?: return
        val siteCode = config.identity.siteCode
        val peerApiPort = siteHa.peerApiPort

        // Resolve advertised host: prefer cloud-delivered address, fall back to local IP
        val peerApiHost = siteHa.peerDirectory
            .firstOrNull { it.agentId == agentId }
            ?.peerApiAdvertisedHost
            ?: getLocalIpAddress()
            ?: return

        val announcement = PeerAnnouncement(
            agentId = agentId,
            siteCode = siteCode,
            peerApiHost = peerApiHost,
            peerApiPort = peerApiPort,
            peerDirectoryVersion = configManager.currentPeerDirectoryVersion,
        )

        try {
            val json = Json.encodeToString(announcement)
            val bytes = json.toByteArray(Charsets.UTF_8)
            DatagramSocket().use { socket ->
                socket.broadcast = true
                val packet = DatagramPacket(
                    bytes,
                    bytes.size,
                    InetAddress.getByName("255.255.255.255"),
                    BROADCAST_PORT,
                )
                socket.send(packet)
            }
            AppLogger.i(TAG, "Broadcast peer announcement: agent=$agentId, site=$siteCode, host=$peerApiHost:$peerApiPort")
        } catch (e: Exception) {
            // Best-effort: LAN may not support broadcast (AP isolation, firewall, etc.)
            AppLogger.w(TAG, "Failed to broadcast peer announcement: ${e.message}")
        }
    }

    private fun getLocalIpAddress(): String? {
        return try {
            NetworkInterface.getNetworkInterfaces()
                ?.toList()
                ?.flatMap { it.inetAddresses.toList() }
                ?.firstOrNull { !it.isLoopbackAddress && it is Inet4Address }
                ?.hostAddress
        } catch (_: Exception) {
            null
        }
    }
}

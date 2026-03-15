package com.fccmiddleware.edge.peer

import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.SocketTimeoutException

/**
 * P2-13: Listens for UDP peer announcements on the station LAN.
 *
 * When a valid PEER_ANNOUNCE is received from a different agent at the same site,
 * this listener:
 * 1. Adds the peer to the local peer coordinator's cache (temporary until cloud confirms)
 * 2. Triggers an immediate config poll to get the authoritative peer directory
 *
 * Announcements from self or from a different siteCode are silently ignored.
 * Only active when `siteHa.enabled = true`.
 *
 * Runs in a coroutine within the provided scope. If the UDP port is already in use
 * or broadcast is blocked, the listener logs a warning and stops without crashing.
 */
class LanPeerListener(
    private val configManager: ConfigManager,
    private val encryptedPrefsManager: EncryptedPrefsManager,
    private val peerCoordinator: PeerCoordinator,
    /** Callback invoked when a valid announcement triggers an immediate config poll. */
    private val onNewPeerDiscovered: (() -> Unit)? = null,
) {

    companion object {
        private const val TAG = "LanPeerListener"
        private const val LISTEN_PORT = LanPeerAnnouncer.BROADCAST_PORT
        /** Socket receive timeout so the loop can check cancellation periodically. */
        private const val RECEIVE_TIMEOUT_MS = 5_000
        /** Maximum datagram size. Peer announcements are small JSON (~300 bytes). */
        private const val MAX_PACKET_SIZE = 2048
    }

    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    @Volatile
    private var listenJob: Job? = null

    /**
     * Start listening for UDP peer announcements. Idempotent — cancels any existing
     * listener before starting a new one.
     */
    fun start(scope: CoroutineScope) {
        stop()
        listenJob = scope.launch(Dispatchers.IO) {
            runListenLoop()
        }
        AppLogger.i(TAG, "LAN peer listener started on port $LISTEN_PORT")
    }

    /** Stop listening. Safe to call even if not started. */
    fun stop() {
        listenJob?.cancel()
        listenJob = null
    }

    private suspend fun runListenLoop() {
        val socket: DatagramSocket
        try {
            socket = DatagramSocket(LISTEN_PORT)
            socket.soTimeout = RECEIVE_TIMEOUT_MS
            socket.reuseAddress = true
        } catch (e: Exception) {
            // Port in use or permission denied — log and exit gracefully
            AppLogger.w(TAG, "Cannot bind to UDP port $LISTEN_PORT: ${e.message}")
            return
        }

        try {
            socket.use { sock ->
                val buffer = ByteArray(MAX_PACKET_SIZE)
                val packet = DatagramPacket(buffer, buffer.size)

                while (kotlinx.coroutines.currentCoroutineContext().isActive) {
                    try {
                        sock.receive(packet)
                        val data = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)
                        handleDatagram(data)
                    } catch (_: SocketTimeoutException) {
                        // Expected — loop back to check cancellation
                    } catch (e: Exception) {
                        if (kotlinx.coroutines.currentCoroutineContext().isActive) {
                            AppLogger.w(TAG, "Error receiving UDP packet: ${e.message}")
                        }
                    }
                }
            }
        } catch (e: Exception) {
            AppLogger.w(TAG, "LAN peer listener stopped unexpectedly: ${e.message}")
        }
    }

    private fun handleDatagram(data: String) {
        val announcement: LanPeerAnnouncer.PeerAnnouncement
        try {
            announcement = json.decodeFromString(LanPeerAnnouncer.PeerAnnouncement.serializer(), data)
        } catch (_: Exception) {
            return // Malformed — silently ignore
        }

        if (announcement.type != "PEER_ANNOUNCE") return

        // Ignore announcements from self
        val localAgentId = encryptedPrefsManager.deviceId ?: return
        if (announcement.agentId == localAgentId) return

        // Ignore announcements from a different site
        val localSiteCode = configManager.config.value?.identity?.siteCode ?: return
        if (announcement.siteCode != localSiteCode) return

        // Check if this is a genuinely new or updated peer
        val existingPeer = peerCoordinator.peers[announcement.agentId]
        val isNewOrUpdated = existingPeer == null
            || existingPeer.peerApiBaseUrl != buildBaseUrl(announcement)

        AppLogger.i(
            TAG,
            "Received peer announcement: agent=${announcement.agentId}, host=${announcement.peerApiHost}:${announcement.peerApiPort}, new=$isNewOrUpdated",
        )

        // Add/update peer in coordinator's directory (temporary, until cloud config confirms)
        val baseUrl = buildBaseUrl(announcement)
        peerCoordinator.peers[announcement.agentId] = PeerState(
            agentId = announcement.agentId,
            deviceClass = "UNKNOWN", // Will be refined on next cloud config poll
            currentRole = "STANDBY_HOT",
            leaderEpoch = 0L,
            peerApiBaseUrl = baseUrl,
            peerDirectoryVersion = announcement.peerDirectoryVersion,
        )

        // Trigger immediate config poll to get authoritative peer directory from cloud
        if (isNewOrUpdated) {
            try {
                onNewPeerDiscovered?.invoke()
            } catch (e: Exception) {
                AppLogger.w(TAG, "Config poll trigger after peer discovery failed: ${e.message}")
            }
        }
    }

    private fun buildBaseUrl(announcement: LanPeerAnnouncer.PeerAnnouncement): String =
        "http://${announcement.peerApiHost}:${announcement.peerApiPort}"
}

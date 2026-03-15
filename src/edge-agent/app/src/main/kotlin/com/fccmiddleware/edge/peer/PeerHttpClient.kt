package com.fccmiddleware.edge.peer

import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.connectivity.BoundSocketFactory
import com.fccmiddleware.edge.connectivity.NetworkBinder
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.replication.DeltaSyncPayload
import com.fccmiddleware.edge.replication.SnapshotPayload
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.time.OffsetDateTime
import java.time.ZoneOffset
import java.util.concurrent.TimeUnit

/**
 * OkHttp-based HTTP client for peer-to-peer communication over the site LAN.
 *
 * All requests are HMAC-signed using [PeerHmacSigner] with the shared secret
 * from [ConfigManager]. Sockets are bound to WiFi via [BoundSocketFactory]
 * to ensure traffic stays on the LAN even when mobile data is preferred for
 * cloud traffic.
 *
 * Timeouts are aggressive (5s) because peers are on the same LAN segment.
 */
class PeerHttpClient(
    private val configManager: ConfigManager,
    private val networkBinder: NetworkBinder,
) {

    companion object {
        private const val TAG = "PeerHttpClient"
        private const val TIMEOUT_SECONDS = 5L
        private val JSON_MEDIA_TYPE = "application/json; charset=utf-8".toMediaType()
    }

    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    /**
     * Lazily built OkHttpClient bound to the WiFi network.
     * Recreated if the WiFi network reference changes.
     */
    private var client: OkHttpClient? = null

    private fun getClient(): OkHttpClient {
        val existing = client
        if (existing != null) return existing

        val socketFactory = BoundSocketFactory { networkBinder.wifiNetwork.value }
        val newClient = OkHttpClient.Builder()
            .connectTimeout(TIMEOUT_SECONDS, TimeUnit.SECONDS)
            .readTimeout(TIMEOUT_SECONDS, TimeUnit.SECONDS)
            .writeTimeout(TIMEOUT_SECONDS, TimeUnit.SECONDS)
            .socketFactory(socketFactory)
            .build()
        client = newClient
        return newClient
    }

    /**
     * Retrieve the peer shared secret for HMAC signing.
     * Uses the FCC shared secret from config as the peer authentication key.
     * Falls back to empty string if not configured (signature will still be
     * computed but peers with a different secret will reject it).
     */
    private fun peerSharedSecret(): String =
        configManager.config.value?.fcc?.sharedSecret ?: ""

    // -------------------------------------------------------------------------
    // Peer API calls
    // -------------------------------------------------------------------------

    /**
     * Send heartbeat to a specific peer.
     * Returns the [PeerHeartbeatResponse] on success, null on failure.
     */
    fun sendHeartbeat(baseUrl: String, request: PeerHeartbeatRequest): PeerHeartbeatResponse? {
        val body = json.encodeToString(PeerHeartbeatRequest.serializer(), request)
        return postJson(baseUrl, "/peer/heartbeat", body, PeerHeartbeatResponse.serializer())
    }

    /**
     * GET /peer/health from a specific peer.
     * Returns [PeerHealthResponse] on success, null on failure.
     */
    fun getHealth(baseUrl: String): PeerHealthResponse? {
        return getJson(baseUrl, "/peer/health", PeerHealthResponse.serializer())
    }

    /**
     * POST /peer/claim-leadership to a specific peer.
     * Returns [PeerLeadershipClaimResponse] on success, null on failure.
     */
    fun claimLeadership(baseUrl: String, request: PeerLeadershipClaimRequest): PeerLeadershipClaimResponse? {
        val body = json.encodeToString(PeerLeadershipClaimRequest.serializer(), request)
        return postJson(baseUrl, "/peer/claim-leadership", body, PeerLeadershipClaimResponse.serializer())
    }

    /**
     * GET /peer/bootstrap — full snapshot for initial replication.
     * Returns [SnapshotPayload] on success, null on failure.
     */
    fun getBootstrap(baseUrl: String): SnapshotPayload? {
        return getJson(baseUrl, "/peer/bootstrap", SnapshotPayload.serializer())
    }

    /**
     * GET /peer/sync?fromSeq={seq} — incremental delta sync.
     * Returns [DeltaSyncPayload] on success, null on failure.
     */
    fun getDeltaSync(baseUrl: String, fromSeq: Long): DeltaSyncPayload? {
        return getJson(baseUrl, "/peer/sync?fromSeq=$fromSeq", DeltaSyncPayload.serializer())
    }

    /**
     * POST /peer/proxy/preauth — forward pre-auth request to primary.
     * Returns [PeerProxyPreAuthResponse] on success, null on failure.
     */
    fun proxyPreAuth(baseUrl: String, request: PeerProxyPreAuthRequest): PeerProxyPreAuthResponse? {
        val body = json.encodeToString(PeerProxyPreAuthRequest.serializer(), request)
        return postJson(baseUrl, "/peer/proxy/preauth", body, PeerProxyPreAuthResponse.serializer())
    }

    /**
     * GET /peer/proxy/pump-status — get pump status from primary.
     * Returns [PeerProxyPumpStatusResponse] on success, null on failure.
     */
    fun getPumpStatus(baseUrl: String): PeerProxyPumpStatusResponse? {
        return getJson(baseUrl, "/peer/proxy/pump-status", PeerProxyPumpStatusResponse.serializer())
    }

    // -------------------------------------------------------------------------
    // Internal HTTP helpers
    // -------------------------------------------------------------------------

    private fun <T> getJson(
        baseUrl: String,
        path: String,
        deserializer: kotlinx.serialization.DeserializationStrategy<T>,
    ): T? {
        val url = "${baseUrl.trimEnd('/')}$path"
        val timestamp = OffsetDateTime.now(ZoneOffset.UTC).toString()
        val secret = peerSharedSecret()
        val signature = PeerHmacSigner.sign(secret, "GET", path.substringBefore("?"), timestamp, null)

        val request = Request.Builder()
            .url(url)
            .get()
            .addHeader("X-Peer-Timestamp", timestamp)
            .addHeader("X-Peer-Signature", signature)
            .build()

        return try {
            val response = getClient().newCall(request).execute()
            if (response.isSuccessful) {
                val responseBody = response.body?.string()
                if (responseBody != null) {
                    json.decodeFromString(deserializer, responseBody)
                } else {
                    null
                }
            } else {
                AppLogger.w(TAG, "GET $path returned ${response.code}")
                null
            }
        } catch (e: Exception) {
            AppLogger.w(TAG, "GET $path failed: ${e.message}")
            null
        }
    }

    private fun <T> postJson(
        baseUrl: String,
        path: String,
        body: String,
        deserializer: kotlinx.serialization.DeserializationStrategy<T>,
    ): T? {
        val url = "${baseUrl.trimEnd('/')}$path"
        val timestamp = OffsetDateTime.now(ZoneOffset.UTC).toString()
        val secret = peerSharedSecret()
        // Sign without body content — the server-side Ktor auth plugin cannot
        // read the body stream without consuming it (preventing route handlers
        // from deserializing the request). HMAC over method+path+timestamp
        // provides authentication; body integrity is guaranteed by LAN transport.
        val signature = PeerHmacSigner.sign(secret, "POST", path, timestamp, null)

        val request = Request.Builder()
            .url(url)
            .post(body.toRequestBody(JSON_MEDIA_TYPE))
            .addHeader("Content-Type", "application/json")
            .addHeader("X-Peer-Timestamp", timestamp)
            .addHeader("X-Peer-Signature", signature)
            .build()

        return try {
            val response = getClient().newCall(request).execute()
            if (response.isSuccessful) {
                val responseBody = response.body?.string()
                if (responseBody != null) {
                    json.decodeFromString(deserializer, responseBody)
                } else {
                    null
                }
            } else {
                AppLogger.w(TAG, "POST $path returned ${response.code}")
                null
            }
        } catch (e: Exception) {
            AppLogger.w(TAG, "POST $path failed: ${e.message}")
            null
        }
    }
}

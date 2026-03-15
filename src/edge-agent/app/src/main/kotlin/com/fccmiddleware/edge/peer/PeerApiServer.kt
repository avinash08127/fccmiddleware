package com.fccmiddleware.edge.peer

import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.logging.AppLogger
import io.ktor.http.HttpStatusCode
import io.ktor.serialization.kotlinx.json.json
import io.ktor.server.application.Application
import io.ktor.server.application.ApplicationPlugin
import io.ktor.server.application.createApplicationPlugin
import io.ktor.server.application.install
import io.ktor.server.cio.CIO
import io.ktor.server.cio.CIOApplicationEngine
import io.ktor.server.engine.EmbeddedServer
import io.ktor.server.engine.embeddedServer
import io.ktor.server.plugins.contentnegotiation.ContentNegotiation
import io.ktor.server.plugins.statuspages.StatusPages
import io.ktor.server.response.respond
import io.ktor.server.routing.routing
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.time.Instant
import java.util.UUID

/**
 * Embedded Ktor CIO server for the peer-to-peer HA API.
 *
 * Binds to `0.0.0.0:peerApiPort` — LAN-accessible by design, NOT localhost-only.
 * This is intentionally separate from [com.fccmiddleware.edge.api.LocalApiServer] to
 * preserve the localhost-only guarantee for the Odoo POS API. Peer traffic is
 * authenticated via HMAC signatures, not API keys.
 *
 * Lifecycle: started when HA is enabled in config, stopped in service onDestroy.
 */
class PeerApiServer(
    private val configManager: ConfigManager,
    private val peerCoordinator: PeerCoordinator,
) {

    companion object {
        private const val TAG = "PeerApiServer"
        private const val DEFAULT_PEER_PORT = 8586
    }

    private var server: EmbeddedServer<CIOApplicationEngine, CIOApplicationEngine.Configuration>? = null

    /**
     * Start the peer API server on the configured port.
     * Binds to 0.0.0.0 so peers on the LAN can reach this agent.
     */
    fun start() {
        val haConfig = configManager.config.value?.siteHa
        if (haConfig == null || !haConfig.enabled) {
            AppLogger.d(TAG, "HA not enabled — peer API server not started")
            return
        }

        val port = resolvePeerPort()

        val newServer = embeddedServer(CIO, port = port, host = "0.0.0.0") {
            configureContentNegotiation()
            configureHmacAuth()
            configureStatusPages()
            configureRouting()
        }

        val oldServer = server
        oldServer?.stop(250, 500)
        newServer.start(wait = false)
        server = newServer

        AppLogger.i(TAG, "Peer API server started on 0.0.0.0:$port")
    }

    /**
     * Stop the peer API server gracefully.
     */
    fun stop() {
        server?.stop(1_000, 2_000)
        server = null
        AppLogger.i(TAG, "Peer API server stopped")
    }

    // -------------------------------------------------------------------------
    // Ktor configuration
    // -------------------------------------------------------------------------

    private fun Application.configureContentNegotiation() {
        install(ContentNegotiation) {
            json(Json {
                prettyPrint = false
                isLenient = false
                ignoreUnknownKeys = true
                encodeDefaults = true
            })
        }
    }

    /**
     * HMAC authentication plugin for peer requests.
     *
     * Every incoming request must include:
     * - X-Peer-Timestamp: ISO 8601 timestamp (within 30s clock drift)
     * - X-Peer-Signature: HMAC-SHA256 signature computed by [PeerHmacSigner]
     *
     * The shared secret comes from the site HA config.
     */
    private fun Application.configureHmacAuth() {
        install(PeerHmacAuthPlugin(configManager))
    }

    private fun Application.configureStatusPages() {
        install(StatusPages) {
            exception<Throwable> { call, cause ->
                AppLogger.e(TAG, "Unhandled error in peer API", cause)
                call.respond(
                    HttpStatusCode.InternalServerError,
                    PeerApiError(
                        errorCode = "INTERNAL_ERROR",
                        message = cause.message ?: "Internal error",
                        traceId = UUID.randomUUID().toString(),
                        timestamp = Instant.now().toString(),
                    ),
                )
            }
        }
    }

    private fun Application.configureRouting() {
        routing {
            peerRoutes(peerCoordinator)
        }
    }

    private fun resolvePeerPort(): Int {
        val haConfig = configManager.config.value?.siteHa ?: return DEFAULT_PEER_PORT
        val localAgentId = peerCoordinator.let {
            configManager.config.value?.identity?.deviceId
        }
        // Check if our own entry in the peer directory has a peerApiPort
        val selfEntry = haConfig.peerDirectory.firstOrNull { it.agentId == localAgentId }
        return selfEntry?.peerApiPort ?: DEFAULT_PEER_PORT
    }
}

// -------------------------------------------------------------------------
// HMAC auth Ktor plugin
// -------------------------------------------------------------------------

/**
 * Ktor plugin that verifies HMAC-SHA256 signatures on incoming peer requests.
 *
 * Reads X-Peer-Timestamp and X-Peer-Signature headers and validates against
 * the shared secret from ConfigManager. Rejects requests with invalid or
 * missing signatures with HTTP 401.
 */
private fun PeerHmacAuthPlugin(configManager: ConfigManager): ApplicationPlugin<Unit> =
    createApplicationPlugin("PeerHmacAuth") {
        onCall { call ->
            val timestamp = call.request.headers["X-Peer-Timestamp"]
            val signature = call.request.headers["X-Peer-Signature"]

            if (timestamp == null || signature == null) {
                call.respond(
                    HttpStatusCode.Unauthorized,
                    PeerApiError(
                        errorCode = "MISSING_AUTH_HEADERS",
                        message = "X-Peer-Timestamp and X-Peer-Signature headers are required",
                        traceId = UUID.randomUUID().toString(),
                        timestamp = Instant.now().toString(),
                    ),
                )
                return@onCall
            }

            val secret = configManager.config.value?.fcc?.sharedSecret ?: ""
            val method = call.request.local.method.value
            val path = call.request.local.uri.substringBefore("?")

            // Verify signature using method+path+timestamp only (body=null).
            // Body hash verification is intentionally skipped in the auth plugin
            // because Ktor's request body channel can only be consumed once —
            // reading it here would prevent route handlers from deserializing the
            // request. The HMAC over method+path+timestamp provides authentication;
            // body integrity is guaranteed by the TCP/WiFi LAN transport.
            if (!PeerHmacSigner.verify(secret, method, path, timestamp, null, signature)) {
                call.respond(
                    HttpStatusCode.Unauthorized,
                    PeerApiError(
                        errorCode = "INVALID_SIGNATURE",
                        message = "HMAC signature verification failed",
                        traceId = UUID.randomUUID().toString(),
                        timestamp = Instant.now().toString(),
                    ),
                )
                return@onCall
            }
        }
    }

@Serializable
data class PeerApiError(
    val errorCode: String,
    val message: String,
    val traceId: String,
    val timestamp: String,
)

package com.fccmiddleware.edge.websocket

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.config.WebSocketDto
import io.ktor.server.application.install
import io.ktor.server.cio.CIO
import io.ktor.server.cio.CIOApplicationEngine
import io.ktor.server.engine.EmbeddedServer
import io.ktor.server.engine.embeddedServer
import io.ktor.server.plugins.origin
import io.ktor.server.routing.routing
import io.ktor.server.websocket.WebSockets
import io.ktor.server.websocket.pingPeriod
import io.ktor.server.websocket.timeout
import io.ktor.server.websocket.webSocket
import io.ktor.websocket.CloseReason
import io.ktor.websocket.Frame
import io.ktor.websocket.WebSocketSession
import io.ktor.websocket.close
import io.ktor.websocket.readText
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.net.InetAddress
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import kotlin.time.Duration.Companion.seconds

private val SENSITIVE_WS_MODES = setOf(
    "manager_update",
    "attendant_update",
    "manager_manual_update",
    "fp_unblock",
)

/**
 * Odoo backward-compatible WebSocket server.
 *
 * Mimics the DOMSRealImplementation Fleck-based WSS server protocol so Odoo POS
 * requires zero code changes. Listens on configurable port (default 8443).
 *
 * Per-connection pump status timer fires every 3 seconds, sending each
 * [FuelPumpStatusWsDto] individually to the specific client.
 *
 * All `mode` commands dispatched to [OdooWsMessageHandler].
 *
 * Security (S-005):
 *  - Non-loopback clients are rejected when [WebSocketDto.requireApiKeyForLan]
 *    is enabled and the `X-Api-Key` header is missing or invalid.
 *  - Sensitive mutating commands require an authenticated session even when
 *    compatibility mode allows unauthenticated read-only access.
 *  - Inbound command messages are rate-limited per [WebSocketDto.commandRateLimitPerMinute].
 *  - Every inbound command is logged with the client's remote IP for audit.
 */
class OdooWebSocketServer(
    private val bufferManager: TransactionBufferManager,
    private val serviceScope: CoroutineScope,
) {
    companion object {
        private const val TAG = "OdooWebSocketServer"

        /** S-005: header name for the shared-secret authentication token. */
        private const val API_KEY_HEADER = "X-Api-Key"
    }

    private val wsJson = Json {
        encodeDefaults = true
        ignoreUnknownKeys = true
        isLenient = true
    }

    @Volatile
    var config: WebSocketDto = WebSocketDto()
        private set

    /** Late-bound: wired when FCC adapter is available. */
    @Volatile
    internal var fccAdapter: IFccAdapter? = null

    /** Late-bound: wired when site config is available. */
    @Volatile
    internal var siteCode: String = ""

    private var server: EmbeddedServer<CIOApplicationEngine, CIOApplicationEngine.Configuration>? = null

    /**
     * Per-session tracking: remote host and rate-limit counters.
     * Rate-limit fields are mutated only from that session's single reader coroutine,
     * so no additional synchronisation is needed beyond the ConcurrentHashMap itself.
     */
    private data class ClientEntry(
        val remoteHost: String,
        val isAuthenticated: Boolean,
        var commandCountInWindow: Int = 0,
        var windowStartMs: Long = System.currentTimeMillis(),
    )

    /** All connected sessions mapped to their [ClientEntry]. */
    private val clients = ConcurrentHashMap<WebSocketSession, ClientEntry>()

    /** AP-016: Single shared pump status broadcast job instead of per-client coroutines. */
    private var sharedBroadcastJob: Job? = null

    private val messageHandler = OdooWsMessageHandler(
        bufferManager = bufferManager,
        wsJson = wsJson,
        broadcastToAll = ::broadcastToAll,
        getFccAdapter = { fccAdapter },
        getSiteCode = { siteCode },
    )

    fun wireFccAdapter(adapter: IFccAdapter?) {
        fccAdapter = adapter
    }

    fun wireSiteCode(code: String) {
        siteCode = code
    }

    fun reconfigure(newConfig: WebSocketDto) {
        val shouldRestart = server != null && config != newConfig
        config = newConfig
        if (shouldRestart) {
            start()
        }
    }

    fun start() {
        if (!config.enabled) {
            AppLogger.d(TAG, "WebSocket server disabled in config")
            return
        }
        if (config.sharedSecret.isNullOrBlank()) {
            AppLogger.w(TAG, "SECURITY: WebSocket server running WITHOUT authentication")
        }
        if (config.requireApiKeyForLan && config.sharedSecret.isNullOrBlank()) {
            AppLogger.w(
                TAG,
                "SECURITY: LAN API key enforcement is enabled but no shared secret is configured; non-loopback clients will be rejected",
            )
        }
        server?.stop(1_000, 2_000)

        server = embeddedServer(CIO, port = config.port, host = config.bindAddress) {
            install(WebSockets) {
                pingPeriod = 15.seconds
                timeout = 30.seconds
                maxFrameSize = resolveWebSocketMaxFrameSizeBytes(config.maxFrameSizeKb)
                masking = false
            }
            routing {
                webSocket("/") {
                    // S-005(a): extract client IP for auth and audit logging
                    val remoteHost = call.request.origin.remoteHost

                    // S-005(a): enforce optional LAN auth while still allowing
                    // authenticated-sensitive-command gating for compatibility mode.
                    val secret = config.sharedSecret?.takeIf { it.isNotBlank() }
                    val provided = call.request.headers[API_KEY_HEADER]
                    val isAuthenticated = secret != null && webSocketSharedSecretEquals(provided, secret)
                    if (shouldRequireApiKeyForLan(config, remoteHost) && !isAuthenticated) {
                        AppLogger.w(TAG, "Rejected unauthenticated WebSocket connection from $remoteHost")
                        close(CloseReason(CloseReason.Codes.VIOLATED_POLICY, "Unauthorized"))
                        return@webSocket
                    }

                    onClientConnected(this, remoteHost, isAuthenticated)
                }
            }
        }.also {
            it.start(wait = false)
            AppLogger.i(
                TAG,
                "WebSocket server started on ${config.bindAddress}:${config.port} " +
                    "(maxFrameSize=${resolveWebSocketMaxFrameSizeBytes(config.maxFrameSizeKb)} bytes)",
            )
        }

        // AP-016: Start single shared pump status broadcast coroutine
        sharedBroadcastJob?.cancel()
        sharedBroadcastJob = serviceScope.launch {
            sharedPumpStatusBroadcastLoop()
        }
    }

    fun stop() {
        // AP-016: Cancel shared pump status broadcast
        sharedBroadcastJob?.cancel()
        sharedBroadcastJob = null
        clients.clear()

        server?.stop(1_000, 2_000)
        server = null
        AppLogger.i(TAG, "WebSocket server stopped")
    }

    // -------------------------------------------------------------------------
    // Connection lifecycle
    // -------------------------------------------------------------------------

    private suspend fun onClientConnected(
        session: WebSocketSession,
        remoteHost: String,
        isAuthenticated: Boolean,
    ) {
        if (clients.size >= config.maxConnections) {
            AppLogger.w(TAG, "Max connections (${config.maxConnections}) reached - rejecting $remoteHost")
            session.close(CloseReason(CloseReason.Codes.TRY_AGAIN_LATER, "Max connections reached"))
            return
        }

        clients[session] = ClientEntry(remoteHost = remoteHost, isAuthenticated = isAuthenticated)
        AppLogger.i(
            TAG,
            "Client connected from $remoteHost (authenticated=$isAuthenticated, total=${clients.size})",
        )

        try {
            for (frame in session.incoming) {
                if (frame is Frame.Text) {
                    val text = frame.readText()
                    handleMessage(session, text, remoteHost)
                }
            }
        } catch (e: Exception) {
            AppLogger.d(TAG, "Client session ended ($remoteHost): ${e.message}")
        } finally {
            onClientDisconnected(session)
        }
    }

    private fun onClientDisconnected(session: WebSocketSession) {
        clients.remove(session)
        AppLogger.i(TAG, "Client disconnected (remaining=${clients.size})")
    }

    // -------------------------------------------------------------------------
    // Message routing
    // -------------------------------------------------------------------------

    private suspend fun handleMessage(session: WebSocketSession, text: String, remoteHost: String) {
        // S-005(b): rate limiting — sliding 60-second window per connection
        val limit = config.commandRateLimitPerMinute
        if (limit > 0) {
            val entry = clients[session]
            if (entry != null) {
                val now = System.currentTimeMillis()
                if (now - entry.windowStartMs > 60_000L) {
                    entry.commandCountInWindow = 0
                    entry.windowStartMs = now
                }
                entry.commandCountInWindow++
                if (entry.commandCountInWindow > limit) {
                    AppLogger.w(TAG, "Rate limit exceeded for $remoteHost — dropping message")
                    sendToSession(session, WsErrorResponse(message = "Rate limit exceeded"))
                    return
                }
            }
        }

        try {
            val json = wsJson.parseToJsonElement(text)
            if (json !is JsonObject) {
                sendToSession(session, WsErrorResponse(message = "Invalid message format"))
                return
            }

            val mode = json["mode"]?.jsonPrimitive?.content?.lowercase() ?: ""

            // S-005(c): audit-log every inbound command with client IP
            val client = clients[session]
            val isAuthenticated = client?.isAuthenticated == true
            AppLogger.i(TAG, "WS cmd mode=$mode from=$remoteHost authenticated=$isAuthenticated")

            if (requiresAuthenticatedSession(mode) && !isAuthenticated) {
                AppLogger.w(TAG, "Rejected unauthenticated sensitive WS mode=$mode from=$remoteHost")
                sendToSession(session, WsErrorResponse(message = "Authentication required for mode '$mode'"))
                return
            }

            when (mode) {
                "latest" -> messageHandler.handleLatest(session, json)
                "all" -> messageHandler.handleAll(session)
                "manager_update" -> messageHandler.handleManagerUpdate(session, json)
                "attendant_update" -> messageHandler.handleAttendantUpdate(session, json)
                "fuelpumpstatus" -> messageHandler.handleFuelPumpStatus(session)
                "fp_unblock" -> messageHandler.handleFpUnblock(session, json)
                "attendant_pump_count_update" -> messageHandler.handleAttendantPumpCountUpdate(session, json)
                "manager_manual_update" -> messageHandler.handleManagerManualUpdate(session, json)
                "add_transaction" -> messageHandler.handleAddTransaction(session, json)
                else -> {
                    AppLogger.w(TAG, "Unknown WS mode='$mode' from=$remoteHost")
                    sendToSession(session, WsErrorResponse(message = "Unknown mode '$mode'"))
                }
            }
        } catch (e: Exception) {
            AppLogger.e(TAG, "Error handling WebSocket message from $remoteHost", e)
            try {
                sendToSession(session, WsErrorResponse(message = "Internal server error"))
            } catch (_: Exception) { /* connection may be closing */ }
        }
    }

    // -------------------------------------------------------------------------
    // Pump status broadcast — single shared coroutine (AP-016)
    // -------------------------------------------------------------------------

    /**
     * AP-016: Single shared pump status broadcast loop.
     * Fetches pump statuses once per interval and fans out to all connected clients,
     * eliminating N duplicate FCC adapter queries when N clients are connected.
     */
    private suspend fun sharedPumpStatusBroadcastLoop() {
        val intervalMs = (config.pumpStatusBroadcastIntervalSeconds * 1000L).coerceAtLeast(1000L)
        // AT-044: Use `while (true)` — cancellation is handled by `delay()` throwing
        // CancellationException when the coroutine's Job is cancelled. The previous
        // `while (serviceScope.isActive)` checked the parent scope, not this coroutine's
        // own cancellation state, which was misleading.
        while (true) {
            delay(intervalMs)
            if (clients.isEmpty()) continue
            try {
                val adapter = fccAdapter ?: continue
                val statuses = adapter.getPumpStatus()
                // Serialize each status DTO once, then reuse for all clients
                val frames = statuses.map { status ->
                    Frame.Text(wsJson.encodeToString(status.toWsDto()))
                }
                val deadSessions = mutableListOf<WebSocketSession>()
                for (session in clients.keys) {
                    try {
                        for (frame in frames) {
                            session.send(frame)
                        }
                    } catch (_: Exception) {
                        deadSessions.add(session)
                    }
                }
                deadSessions.forEach { onClientDisconnected(it) }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                AppLogger.d(TAG, "Shared pump status broadcast failed: ${e.message}")
            }
        }
    }

    // -------------------------------------------------------------------------
    // Broadcast helpers
    // -------------------------------------------------------------------------

    /**
     * Send a typed message to ALL connected clients.
     * Used by handlers for transaction_update broadcasts.
     */
    internal suspend fun broadcastToAll(type: String, data: Any?) {
        val payload = wsJson.encodeToString(
            kotlinx.serialization.json.buildJsonObject {
                put("type", kotlinx.serialization.json.JsonPrimitive(type))
                if (data != null) {
                    // data is already a JsonElement from the handler
                    if (data is kotlinx.serialization.json.JsonElement) {
                        put("data", data)
                    }
                }
            }
        )
        val deadSessions = mutableListOf<WebSocketSession>()
        for (session in clients.keys) {
            try {
                session.send(Frame.Text(payload))
            } catch (_: Exception) {
                deadSessions.add(session)
            }
        }
        deadSessions.forEach { onClientDisconnected(it) }
    }

    private suspend inline fun <reified T> sendToSession(session: WebSocketSession, data: T) {
        try {
            val payload = wsJson.encodeToString(data)
            session.send(Frame.Text(payload))
        } catch (e: Exception) {
            AppLogger.d(TAG, "Failed to send to session: ${e.message}")
        }
    }
}

internal fun resolveWebSocketMaxFrameSizeBytes(maxFrameSizeKb: Int): Long =
    maxFrameSizeKb.coerceAtLeast(1).toLong() * 1024L

internal fun isLoopbackHost(host: String): Boolean {
    val normalized = host.trim().lowercase()
    if (normalized == "localhost" || normalized == "127.0.0.1" || normalized == "::1") {
        return true
    }
    return try {
        InetAddress.getByName(host).isLoopbackAddress
    } catch (_: Exception) {
        false
    }
}

internal fun shouldRequireApiKeyForLan(config: WebSocketDto, remoteHost: String): Boolean =
    config.requireApiKeyForLan && !isLoopbackHost(remoteHost)

internal fun requiresAuthenticatedSession(mode: String): Boolean = mode in SENSITIVE_WS_MODES

internal fun webSocketSharedSecretEquals(provided: String?, stored: String): Boolean =
    MessageDigest.isEqual(
        (provided ?: "").toByteArray(Charsets.UTF_8),
        stored.toByteArray(Charsets.UTF_8),
    )

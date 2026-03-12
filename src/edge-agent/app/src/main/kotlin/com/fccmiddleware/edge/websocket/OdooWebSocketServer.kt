package com.fccmiddleware.edge.websocket

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.config.WebSocketDto
import io.ktor.server.application.install
import io.ktor.server.cio.CIO
import io.ktor.server.cio.CIOApplicationEngine
import io.ktor.server.engine.EmbeddedServer
import io.ktor.server.engine.embeddedServer
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
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlin.time.Duration.Companion.seconds
import java.util.concurrent.ConcurrentHashMap

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
 */
class OdooWebSocketServer(
    private val transactionDao: TransactionBufferDao,
    private val serviceScope: CoroutineScope,
) {
    companion object {
        private const val TAG = "OdooWebSocketServer"
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

    /** All connected sessions. Value = pump-status broadcast job for that session. */
    private val clients = ConcurrentHashMap<WebSocketSession, Job>()

    private val messageHandler = OdooWsMessageHandler(
        transactionDao = transactionDao,
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
        server?.stop(1_000, 2_000)

        server = embeddedServer(CIO, port = config.port, host = config.bindAddress) {
            install(WebSockets) {
                pingPeriod = 15.seconds
                timeout = 30.seconds
                maxFrameSize = Long.MAX_VALUE
                masking = false
            }
            routing {
                webSocket("/") {
                    onClientConnected(this)
                }
            }
        }.also {
            it.start(wait = false)
            AppLogger.i(TAG, "WebSocket server started on ${config.bindAddress}:${config.port}")
        }
    }

    fun stop() {
        // Cancel all pump-status broadcast jobs
        clients.values.forEach { it.cancel() }
        clients.clear()

        server?.stop(1_000, 2_000)
        server = null
        AppLogger.i(TAG, "WebSocket server stopped")
    }

    // -------------------------------------------------------------------------
    // Connection lifecycle
    // -------------------------------------------------------------------------

    private suspend fun onClientConnected(session: WebSocketSession) {
        if (clients.size >= config.maxConnections) {
            AppLogger.w(TAG, "Max connections (${config.maxConnections}) reached — rejecting client")
            session.close(CloseReason(CloseReason.Codes.TRY_AGAIN_LATER, "Max connections reached"))
            return
        }

        // Start per-connection pump status broadcast timer
        val broadcastJob = serviceScope.launch {
            pumpStatusBroadcastLoop(session)
        }
        clients[session] = broadcastJob
        AppLogger.i(TAG, "Client connected (total=${clients.size})")

        try {
            for (frame in session.incoming) {
                if (frame is Frame.Text) {
                    val text = frame.readText()
                    handleMessage(session, text)
                }
            }
        } catch (e: Exception) {
            AppLogger.d(TAG, "Client session ended: ${e.message}")
        } finally {
            onClientDisconnected(session)
        }
    }

    private fun onClientDisconnected(session: WebSocketSession) {
        clients.remove(session)?.cancel()
        AppLogger.i(TAG, "Client disconnected (remaining=${clients.size})")
    }

    // -------------------------------------------------------------------------
    // Message routing
    // -------------------------------------------------------------------------

    private suspend fun handleMessage(session: WebSocketSession, text: String) {
        try {
            val json = wsJson.parseToJsonElement(text)
            if (json !is JsonObject) {
                sendToSession(session, WsErrorResponse(message = "Invalid message format"))
                return
            }

            val mode = json["mode"]?.jsonPrimitive?.content?.lowercase() ?: ""

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
                    sendToSession(session, WsErrorResponse(message = "Unknown mode '$mode'"))
                }
            }
        } catch (e: Exception) {
            AppLogger.e(TAG, "Error handling WebSocket message", e)
            try {
                sendToSession(session, WsErrorResponse(message = "Internal server error"))
            } catch (_: Exception) { /* connection may be closing */ }
        }
    }

    // -------------------------------------------------------------------------
    // Pump status broadcast — per-connection timer (every 3s)
    // -------------------------------------------------------------------------

    private suspend fun pumpStatusBroadcastLoop(session: WebSocketSession) {
        val intervalMs = (config.pumpStatusBroadcastIntervalSeconds * 1000L).coerceAtLeast(1000L)
        while (serviceScope.isActive) {
            delay(intervalMs)
            try {
                val adapter = fccAdapter ?: continue
                val statuses = adapter.getPumpStatus()
                for (status in statuses) {
                    val dto = status.toWsDto()
                    val payload = wsJson.encodeToString(dto)
                    session.send(Frame.Text(payload))
                }
            } catch (e: Exception) {
                AppLogger.d(TAG, "Pump status broadcast failed for session: ${e.message}")
                break // session likely closed
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

package com.fccmiddleware.edge.adapter.advatec

import com.fccmiddleware.edge.logging.AppLogger
import io.ktor.http.ContentType
import io.ktor.http.HttpStatusCode
import io.ktor.server.cio.CIO
import io.ktor.server.engine.EmbeddedServer
import io.ktor.server.engine.embeddedServer
import io.ktor.server.request.header
import io.ktor.server.request.receiveText
import io.ktor.server.response.respondText
import io.ktor.server.routing.post
import io.ktor.server.routing.routing
import kotlinx.coroutines.CancellationException
import java.time.Instant
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * Advatec Receipt webhook listener (ADV-3.2).
 *
 * Listens for Receipt webhook callbacks from the Advatec EFD device on a
 * configurable port. Validates the webhook token (X-Webhook-Token header or
 * ?token= query parameter), then enqueues valid payloads for the adapter to
 * drain and normalize.
 *
 * Always returns HTTP 200 OK — Advatec retry behaviour is unknown (AQ-7),
 * so we avoid triggering potential retries on validation errors.
 *
 * Thread safety: The incoming queue is a lock-free [ConcurrentLinkedQueue].
 * The server runs on CIO (coroutine I/O) threads — no Android main thread blocking.
 */
class AdvatecWebhookListener(
    /** Port for the embedded HTTP server to listen on. */
    private val listenPort: Int,
    /** Site code to stamp on received payloads. */
    private val siteCode: String,
    /** Expected webhook token for authentication. Null/blank = skip validation. */
    private val webhookToken: String?,
) {
    companion object {
        private const val TAG = "AdvatecWebhookListener"

        /** Maximum queued receipts before dropping (back-pressure safety). */
        const val MAX_QUEUE_SIZE = 10_000
    }

    /** Thread-safe queue of raw JSON payloads received from the Advatec EFD. */
    private val incomingQueue = ConcurrentLinkedQueue<ReceivedPayload>()

    /** Atomic counter tracking queue size to avoid TOCTOU race. */
    private val queueCount = AtomicInteger(0)

    /** Whether the listener is currently running. */
    private val running = AtomicBoolean(false)

    /** Embedded Ktor server instance. Null when stopped. */
    private var server: EmbeddedServer<*, *>? = null

    /**
     * Whether the listener is currently accepting connections.
     */
    val isRunning: Boolean
        get() = running.get()

    /**
     * Number of payloads currently queued and not yet drained by the adapter.
     */
    val queueSize: Int
        get() = incomingQueue.size

    /**
     * Starts the embedded HTTP server.
     *
     * Idempotent — calling start on an already-running listener is a no-op.
     * The server binds to 0.0.0.0:[listenPort] and accepts POSTs on /api/webhook/advatec.
     *
     * @return true if the server started (or was already running), false on bind failure
     */
    fun start(): Boolean {
        if (running.getAndSet(true)) {
            AppLogger.d(TAG, "Already running on port $listenPort")
            return true
        }

        return try {
            val srv = embeddedServer(CIO, port = listenPort) {
                routing {
                    post("/api/webhook/advatec") {
                        handleWebhookRequest(call)
                    }
                    // Also accept on catch-all for flexibility (Advatec webhook URL may vary)
                    post("/api/webhook/advatec/") {
                        handleWebhookRequest(call)
                    }
                }
            }
            srv.start(wait = false)
            server = srv
            AppLogger.i(TAG, "Started on port $listenPort (path: /api/webhook/advatec)")
            true
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to start on port $listenPort: ${e.message}")
            running.set(false)
            false
        }
    }

    /**
     * Stops the embedded HTTP server and clears the incoming queue.
     *
     * Idempotent — calling stop on an already-stopped listener is a no-op.
     * Waits up to 2 seconds for graceful shutdown.
     */
    fun stop() {
        if (!running.getAndSet(false)) {
            return
        }

        try {
            server?.stop(gracePeriodMillis = 1000, timeoutMillis = 2000)
            AppLogger.i(TAG, "Stopped")
        } catch (e: Exception) {
            AppLogger.w(TAG, "Error during shutdown: ${e.message}")
        } finally {
            server = null
            incomingQueue.clear()
            queueCount.set(0)
        }
    }

    /**
     * Drains all queued payloads (up to [maxCount]) and returns them.
     *
     * Non-blocking. Returns an empty list if no payloads are available.
     * The adapter calls this during its fetch cycle to collect pushed receipts.
     *
     * @param maxCount Maximum number of payloads to drain in one call
     * @return List of received payloads, oldest first
     */
    fun drainQueue(maxCount: Int = 200): List<ReceivedPayload> {
        val result = mutableListOf<ReceivedPayload>()
        repeat(maxCount) {
            val item = incomingQueue.poll() ?: return result
            queueCount.decrementAndGet()
            result.add(item)
        }
        return result
    }

    // -----------------------------------------------------------------------
    // Private — Request handling
    // -----------------------------------------------------------------------

    /**
     * Handles an incoming HTTP POST from the Advatec EFD.
     *
     * Validation steps:
     * 1. Read raw JSON body
     * 2. Validate webhook token (X-Webhook-Token header or ?token= param)
     * 3. Enqueue the raw JSON for later normalization
     * 4. Return HTTP 200 OK
     *
     * Always returns 200 — Advatec retry behaviour is unknown (AQ-7).
     */
    private suspend fun handleWebhookRequest(call: io.ktor.server.application.ApplicationCall) {
        try {
            // Step 1: Read raw JSON body
            val rawJson = call.receiveText()
            if (rawJson.isBlank()) {
                AppLogger.w(TAG, "Received empty body")
                call.respondText("OK", ContentType.Text.Plain, HttpStatusCode.OK)
                return
            }

            // Step 2: Validate webhook token
            if (!webhookToken.isNullOrBlank()) {
                val providedToken = call.request.header("X-Webhook-Token")
                    ?: call.request.queryParameters["token"]

                if (providedToken.isNullOrBlank() || providedToken != webhookToken) {
                    AppLogger.w(TAG, "Rejected request with invalid or missing webhook token")
                    // Still return 200 to avoid leaking token validation info
                    call.respondText("OK", ContentType.Text.Plain, HttpStatusCode.OK)
                    return
                }
            }

            // Step 3: Enqueue for adapter processing (with back-pressure guard)
            val newCount = queueCount.incrementAndGet()
            if (newCount > MAX_QUEUE_SIZE) {
                queueCount.decrementAndGet()
                AppLogger.w(TAG, "Queue full ($MAX_QUEUE_SIZE) — dropping receipt")
                call.respondText("OK", ContentType.Text.Plain, HttpStatusCode.OK)
                return
            }

            val payload = ReceivedPayload(
                rawJson = rawJson,
                siteCode = siteCode,
                receivedAt = Instant.now(),
            )
            incomingQueue.add(payload)
            AppLogger.d(TAG, "Enqueued receipt webhook (queue size=$newCount, body=${rawJson.length} bytes)")

            // Step 4: Return 200 OK
            call.respondText("OK", ContentType.Text.Plain, HttpStatusCode.OK)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            AppLogger.e(TAG, "Error handling webhook request: ${e::class.simpleName}: ${e.message}")
            try {
                call.respondText("OK", ContentType.Text.Plain, HttpStatusCode.OK)
            } catch (_: Exception) {
                // Response already sent or connection closed — ignore
            }
        }
    }
}

/**
 * A raw receipt payload received via the Advatec webhook.
 *
 * Stored in the [AdvatecWebhookListener]'s queue until drained by the adapter
 * for normalization.
 */
data class ReceivedPayload(
    /** Complete raw JSON body from the webhook POST request. */
    val rawJson: String,
    /** Site code stamped on receipt. */
    val siteCode: String,
    /** When the webhook was received by the listener. */
    val receivedAt: Instant,
)

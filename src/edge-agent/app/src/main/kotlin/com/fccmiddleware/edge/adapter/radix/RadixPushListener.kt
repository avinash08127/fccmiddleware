package com.fccmiddleware.edge.adapter.radix

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
 * Radix unsolicited push listener (RX-5.1).
 *
 * When the Radix FDC operates in UNSOLICITED mode (MODE=2), it pushes completed
 * transaction notifications to the edge agent via HTTP POST instead of waiting
 * for the agent to poll. This listener:
 *
 * 1. Starts an embedded HTTP server (Ktor CIO) on a configurable port
 * 2. Accepts POST requests containing `<FDC_RESP>` XML with RESP_CODE=30
 * 3. Validates the USN-Code header and SHA-1 signature via [RadixSignatureHelper]
 * 4. Returns an XML ACK (`<HOST_REQ>` with CMD_CODE=201) to the FDC
 * 5. Enqueues validated transaction XML into a [ConcurrentLinkedQueue] for
 *    the adapter to drain and normalize
 * 6. Provides [start]/[stop] lifecycle methods
 *
 * Thread safety: The incoming queue is a lock-free [ConcurrentLinkedQueue].
 * The server runs on CIO (coroutine I/O) threads — no Android main thread blocking.
 *
 * The FDC expects the listener at a known host:port and will retry on failure.
 * Signature validation prevents spoofed transactions from rogue LAN devices.
 */
class RadixPushListener(
    /** Port for the embedded HTTP server to listen on. */
    private val listenPort: Int,
    /** Expected USN-Code header value from the FDC. */
    private val expectedUsnCode: Int,
    /** Shared secret for SHA-1 signature validation. */
    private val sharedSecret: String,
) {
    companion object {
        private const val TAG = "RadixPushListener"

        /** RESP_CODE for unsolicited push transactions from the FDC. */
        const val RESP_CODE_UNSOLICITED = 30

        /** Maximum queued transactions before dropping (back-pressure safety). */
        const val MAX_QUEUE_SIZE = 10_000
    }

    /** Thread-safe queue of raw XML payloads received from the FDC. */
    private val incomingQueue = ConcurrentLinkedQueue<PushedTransaction>()

    /** Atomic counter tracking queue size to avoid TOCTOU race on size check + enqueue. */
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
     * Number of transactions currently queued and not yet drained by the adapter.
     */
    val queueSize: Int
        get() = incomingQueue.size

    /**
     * Starts the embedded HTTP server.
     *
     * Idempotent — calling start on an already-running listener is a no-op.
     * The server binds to 0.0.0.0:[listenPort] and accepts POSTs on any path.
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
                    post("{...}") {
                        handlePushRequest(call)
                    }
                }
            }
            srv.start(wait = false)
            server = srv
            AppLogger.i(TAG, "Started on port $listenPort")
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
     * Drains all queued pushed transactions (up to [maxCount]) and returns them.
     *
     * Non-blocking. Returns an empty list if no transactions are available.
     * The adapter calls this during its fetch cycle to collect pushed transactions.
     *
     * @param maxCount Maximum number of transactions to drain in one call
     * @return List of pushed transaction payloads, oldest first
     */
    fun drainQueue(maxCount: Int = 200): List<PushedTransaction> {
        val result = mutableListOf<PushedTransaction>()
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
     * Handles an incoming HTTP POST from the FDC.
     *
     * Validation steps:
     * 1. Read raw XML body
     * 2. Validate USN-Code header matches expected value
     * 3. Parse XML to extract RESP_CODE (must be 30 for unsolicited push)
     * 4. Validate SHA-1 signature using [RadixSignatureHelper]
     * 5. Enqueue the raw XML for later normalization
     * 6. Return XML ACK (CMD_CODE=201) to the FDC
     *
     * On any validation failure, returns HTTP 400 with an error XML body.
     * The FDC will retry on non-200 responses.
     */
    private suspend fun handlePushRequest(call: io.ktor.server.application.ApplicationCall) {
        try {
            // Step 1: Read raw XML body
            val rawXml = call.receiveText()
            if (rawXml.isBlank()) {
                AppLogger.w(TAG, "Received empty body")
                call.respondText(
                    buildErrorResponse("Empty request body"),
                    ContentType.Application.Xml,
                    HttpStatusCode.BadRequest,
                )
                return
            }

            // Step 2: Validate USN-Code header
            val usnHeader = call.request.header("USN-Code")?.trim()?.toIntOrNull()
            if (usnHeader == null || usnHeader != expectedUsnCode) {
                AppLogger.w(TAG, "USN-Code mismatch: expected=$expectedUsnCode, received=$usnHeader")
                call.respondText(
                    buildErrorResponse("Invalid USN-Code"),
                    ContentType.Application.Xml,
                    HttpStatusCode.BadRequest,
                )
                return
            }

            // Step 3: Parse XML to verify it is a valid unsolicited transaction
            val parseResult = RadixXmlParser.parseTransactionResponse(rawXml)
            when (parseResult) {
                is RadixParseResult.Error -> {
                    AppLogger.w(TAG, "Failed to parse pushed XML: ${parseResult.message}")
                    call.respondText(
                        buildErrorResponse("Invalid XML"),
                        ContentType.Application.Xml,
                        HttpStatusCode.BadRequest,
                    )
                    return
                }
                is RadixParseResult.Success -> {
                    val resp = parseResult.value
                    if (resp.respCode != RESP_CODE_UNSOLICITED && resp.respCode != RadixAdapter.RESP_CODE_SUCCESS) {
                        AppLogger.w(TAG, "Unexpected RESP_CODE=${resp.respCode} in pushed transaction")
                        call.respondText(
                            buildErrorResponse("Unexpected RESP_CODE"),
                            ContentType.Application.Xml,
                            HttpStatusCode.BadRequest,
                        )
                        return
                    }

                    if (resp.transaction == null) {
                        AppLogger.w(TAG, "No TRN element in pushed transaction (RESP_CODE=${resp.respCode})")
                        call.respondText(
                            buildErrorResponse("Missing TRN element"),
                            ContentType.Application.Xml,
                            HttpStatusCode.BadRequest,
                        )
                        return
                    }
                }
            }

            // Step 4: Validate SHA-1 signature
            if (sharedSecret.isNotBlank()) {
                val signatureValid = RadixXmlParser.validateTransactionResponseSignature(rawXml, sharedSecret)
                if (!signatureValid) {
                    AppLogger.w(TAG, "Signature validation failed for pushed transaction")
                    call.respondText(
                        buildErrorResponse("Signature validation failed"),
                        ContentType.Application.Xml,
                        HttpStatusCode.Forbidden,
                    )
                    return
                }
            }

            // Step 5: Enqueue for adapter processing (with back-pressure guard).
            // Use atomic increment-then-check to avoid TOCTOU race between
            // concurrent push requests that could each pass a size check
            // before any of them enqueue.
            val newCount = queueCount.incrementAndGet()
            if (newCount > MAX_QUEUE_SIZE) {
                queueCount.decrementAndGet()
                AppLogger.w(TAG, "Queue full ($MAX_QUEUE_SIZE) — dropping transaction")
                call.respondText(
                    buildErrorResponse("Queue full"),
                    ContentType.Application.Xml,
                    HttpStatusCode.ServiceUnavailable,
                )
                return
            }

            val pushed = PushedTransaction(
                rawXml = rawXml,
                receivedAt = Instant.now(),
            )
            incomingQueue.add(pushed)
            AppLogger.d(TAG, "Enqueued pushed transaction (queue size=$newCount)")

            // Step 6: Return XML ACK to FDC
            call.respondText(
                buildAckResponse(),
                ContentType.Application.Xml,
                HttpStatusCode.OK,
            )
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            AppLogger.e(TAG, "Error handling push request: ${e::class.simpleName}: ${e.message}")
            try {
                call.respondText(
                    buildErrorResponse("Internal error"),
                    ContentType.Application.Xml,
                    HttpStatusCode.InternalServerError,
                )
            } catch (_: Exception) {
                // Response already sent or connection closed — ignore
            }
        }
    }

    // -----------------------------------------------------------------------
    // Private — XML response builders
    // -----------------------------------------------------------------------

    /**
     * Builds the XML ACK response sent back to the FDC after a successful push.
     *
     * The FDC expects a HOST_REQ-style acknowledgment with CMD_CODE=201
     * to confirm the transaction was received. Delegates to
     * [RadixXmlBuilder.buildTransactionAck] to include a proper SHA-1 signature,
     * matching the pull-mode ACK format.
     */
    private fun buildAckResponse(): String {
        return RadixXmlBuilder.buildTransactionAck(token = "0", secret = sharedSecret)
    }

    /**
     * Builds an error XML response for the FDC.
     */
    private fun buildErrorResponse(message: String): String {
        return StringBuilder().apply {
            append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            append("<HOST_REQ>\n")
            append("<REQ>\n")
            append("    <CMD_CODE>255</CMD_CODE>\n")
            append("    <CMD_NAME>ERROR</CMD_NAME>\n")
            append("    <MSG>").append(escapeXml(message)).append("</MSG>\n")
            append("</REQ>\n")
            append("</HOST_REQ>")
        }.toString()
    }

    private fun escapeXml(value: String): String {
        return value
            .replace("&", "&amp;")
            .replace("<", "&lt;")
            .replace(">", "&gt;")
            .replace("\"", "&quot;")
            .replace("'", "&apos;")
    }
}

/**
 * A raw transaction payload pushed by the FDC in UNSOLICITED mode.
 *
 * Stored in the [RadixPushListener]'s queue until drained by the adapter
 * for normalization.
 */
data class PushedTransaction(
    /** Complete raw XML body from the FDC POST request. */
    val rawXml: String,
    /** When the push was received by the listener. */
    val receivedAt: Instant,
)

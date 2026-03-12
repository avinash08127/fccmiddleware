package com.fccmiddleware.edge.adapter.radix

import android.util.Log
import com.fccmiddleware.edge.adapter.common.*
import io.ktor.client.HttpClient
import io.ktor.client.engine.okhttp.OkHttp
import io.ktor.client.request.header
import io.ktor.client.request.post
import io.ktor.client.request.setBody
import io.ktor.client.statement.bodyAsText
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout
import java.util.concurrent.atomic.AtomicInteger

/**
 * RadixAdapter — Edge Agent adapter for the Radix FCC protocol.
 *
 * Communicates with the FCC over station LAN using HTTP POST with XML bodies:
 *   Auth port      : P (from config authPort) — external authorization (pre-auth)
 *   Transaction port: P+1 — transaction management, products, day close, ATG, CSR
 *   Signing        : SHA-1 hash of XML body + shared secret password
 *   Heartbeat      : CMD_CODE=55 (product/price read) — no dedicated endpoint
 *   Fetch          : FIFO drain loop: CMD_CODE=10 (request) → CMD_CODE=201 (ACK) → repeat
 *   Pre-auth       : <AUTH_DATA> XML to auth port P
 *   Pump status    : Not supported by Radix protocol
 *
 * Full implementation follows RX-1.x tasks.
 */
class RadixAdapter(
    private val config: AgentFccConfig,
    private val httpClient: HttpClient = HttpClient(OkHttp),
) : IFccAdapter {

    /** Sequential token counter for heartbeat requests (0–65535, wraps at 65536). */
    private val heartbeatTokenCounter = AtomicInteger(0)

    /** Transaction management port = authPort + 1. Falls back to config.port + 1 if authPort not set. */
    private val transactionPort: Int
        get() = (config.authPort ?: config.port) + 1

    private val sharedSecret: String
        get() = config.sharedSecret ?: ""

    private val usnCode: Int
        get() = config.usnCode ?: 0

    /** Generates the next sequential token (0–65535), wrapping at 65536. Thread-safe. */
    private fun nextHeartbeatToken(): String {
        val current = heartbeatTokenCounter.getAndUpdate { (it + 1) % 65536 }
        return current.toString()
    }

    override suspend fun normalize(rawPayload: RawPayloadEnvelope): NormalizationResult {
        return NormalizationResult.Failure(
            errorCode = "UNSUPPORTED_MESSAGE_TYPE",
            message = "Radix adapter is not yet implemented (RX-1.x). Select a supported FCC vendor.",
        )
    }

    override suspend fun sendPreAuth(command: PreAuthCommand): PreAuthResult {
        return PreAuthResult(
            status = PreAuthResultStatus.ERROR,
            message = "Radix adapter is not yet implemented (RX-1.x). Select a supported FCC vendor.",
        )
    }

    /** Radix does not expose real-time pump status. Always returns empty list. */
    override suspend fun getPumpStatus(): List<PumpStatus> = emptyList()

    /**
     * Liveness probe using CMD_CODE=55 (product/price read) on port P+1.
     *
     * Returns true only when the FCC responds with RESP_CODE=201.
     * Never throws — returns false on any error (network, timeout, parse failure, signature).
     * Enforces a 5-second hard timeout.
     * Signature errors (RESP_CODE=251) are logged as warnings (config issue, not transient).
     */
    override suspend fun heartbeat(): Boolean {
        return try {
            withTimeout(HEARTBEAT_TIMEOUT_MS) {
                val token = nextHeartbeatToken()
                val requestBody = RadixXmlBuilder.buildProductReadRequest(token, sharedSecret)
                val headers = RadixXmlBuilder.buildHttpHeaders(usnCode, RadixXmlBuilder.OPERATION_PRODUCTS)
                val url = "http://${config.hostAddress}:$transactionPort"

                val response = httpClient.post(url) {
                    headers.forEach { (key, value) -> header(key, value) }
                    setBody(requestBody)
                }

                val responseBody = response.bodyAsText()
                when (val parseResult = RadixXmlParser.parseProductResponse(responseBody)) {
                    is RadixParseResult.Success -> {
                        if (parseResult.value.respCode == RESP_CODE_SIGNATURE_ERROR) {
                            Log.w(TAG, "Heartbeat: signature error (RESP_CODE=251) — check sharedSecret configuration")
                            false
                        } else {
                            parseResult.value.respCode == RESP_CODE_SUCCESS
                        }
                    }
                    is RadixParseResult.Error -> {
                        Log.w(TAG, "Heartbeat: failed to parse response: ${parseResult.message}")
                        false
                    }
                }
            }
        } catch (e: TimeoutCancellationException) {
            Log.d(TAG, "Heartbeat: timeout after ${HEARTBEAT_TIMEOUT_MS}ms")
            false
        } catch (e: CancellationException) {
            throw e // Preserve structured concurrency cancellation
        } catch (e: Exception) {
            Log.d(TAG, "Heartbeat: ${e::class.simpleName}: ${e.message}")
            false
        }
    }

    override suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch {
        return TransactionBatch(
            transactions = emptyList(),
            hasMore = false,
        )
    }

    /** No-op — Radix ACK (CMD_CODE=201) is sent inline during the fetch loop. */
    override suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean = true

    companion object {
        private const val TAG = "RadixAdapter"
        val VENDOR = FccVendor.RADIX
        const val ADAPTER_VERSION = "1.0.0"
        const val PROTOCOL = "HTTP_XML"

        /** Returns true if this adapter has a working implementation. */
        const val IS_IMPLEMENTED = false

        /** Hard timeout for heartbeat probe (5 seconds). */
        const val HEARTBEAT_TIMEOUT_MS = 5000L

        /** Successful response code. */
        const val RESP_CODE_SUCCESS = 201

        /** Signature error response code — indicates misconfigured shared secret. */
        const val RESP_CODE_SIGNATURE_ERROR = 251
    }
}

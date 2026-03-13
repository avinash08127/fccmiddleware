package com.fccmiddleware.edge.adapter.advatec

import com.fccmiddleware.edge.adapter.common.*
import com.fccmiddleware.edge.logging.AppLogger
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.json.Json
import java.io.OutputStreamWriter
import java.math.BigDecimal
import java.math.RoundingMode
import java.net.HttpURLConnection
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URL
import kotlin.coroutines.cancellation.CancellationException

/**
 * Fiscalization service that generates TRA-compliant fiscal receipts via an Advatec EFD device (ADV-7.2).
 *
 * Used in Scenario A where the primary FCC (DOMS/Radix) controls pumps and provides
 * transaction data, and Advatec is a secondary localhost device used solely for fiscal
 * receipt generation. The flow is:
 *   1. Primary FCC transaction is ingested and buffered
 *   2. This service POSTs Customer data to Advatec with transaction details
 *   3. Advatec generates a TRA fiscal receipt and sends it via webhook
 *   4. The fiscal receipt data is attached to the original transaction
 *
 * Thread safety: A [Mutex] serializes fiscalization requests because Advatec is a
 * single-threaded localhost device processing requests sequentially.
 */
class AdvatecFiscalizationService(private val config: AgentFccConfig) : IFiscalizationService {

    companion object {
        private const val TAG = "AdvatecFiscalizationSvc"
        private const val RECEIPT_TIMEOUT_MS = 30_000L
        private const val HEARTBEAT_TIMEOUT_MS = 5000
        private const val DEFAULT_DEVICE_PORT = 5560
        /** AF-021: Uses port 8092 by default to avoid conflict with AdvatecAdapter (8091). */
        private const val DEFAULT_FISCAL_WEBHOOK_LISTENER_PORT = 8092
        private const val SUBMIT_TIMEOUT_MS = 10_000
        private const val DRAIN_INTERVAL_MS = 100L

        private val json = Json {
            ignoreUnknownKeys = true
            isLenient = true
        }

        private val MICROLITRES_PER_LITRE = BigDecimal("1000000")
    }

    // ── Webhook listener ─────────────────────────────────────────────────────

    private var webhookListener: AdvatecWebhookListener? = null

    @Volatile
    private var initialized = false

    // AP-013: Channel replaces ConcurrentLinkedQueue + busy-wait polling.
    // The drain coroutine sends receipts here; submitForFiscalization suspends on receive.
    private val receiptChannel = Channel<AdvatecReceiptData>(Channel.UNLIMITED)

    // Coroutine scope for the webhook drain coroutine (replaces daemon thread)
    private var drainScope: CoroutineScope? = null

    // ── Serialize fiscalization ──────────────────────────────────────────────

    private val fiscalizeMutex = Mutex()

    // ── IFiscalizationService ─────────────────────────────────────────────────

    override suspend fun submitForFiscalization(
        transaction: CanonicalTransaction,
        context: FiscalizationContext,
    ): FiscalizationResult {
        ensureInitialized()

        return fiscalizeMutex.withLock {
            // Drain any stale receipts from prior calls
            while (receiptChannel.tryReceive().isSuccess) { /* drain */ }

            val host = config.advatecDeviceAddress ?: "127.0.0.1"
            val port = config.advatecDevicePort ?: DEFAULT_DEVICE_PORT

            // Convert volume back to litres for Advatec Dose field
            val doseLitres = BigDecimal(transaction.volumeMicrolitres)
                .divide(MICROLITRES_PER_LITRE, 4, RoundingMode.HALF_UP)

            val custIdType = context.customerIdType
                ?: config.advatecCustIdType
                ?: 6 // 6 = NIL (TRA default)

            // Build payment from transaction amount (convert minor → major units)
            val currencyFactor = getCurrencyFactor(transaction.currencyCode)
            val amountMajor = if (currencyFactor.compareTo(BigDecimal.ZERO) != 0) {
                BigDecimal(transaction.amountMinorUnits)
                    .divide(currencyFactor, 4, RoundingMode.HALF_UP)
            } else {
                BigDecimal.ZERO
            }

            val request = AdvatecCustomerRequest(
                dataType = "Customer",
                data = AdvatecCustomerData(
                    pump = transaction.pumpNumber,
                    dose = doseLitres,
                    custIdType = custIdType,
                    customerId = context.customerTaxId ?: "",
                    customerName = context.customerName ?: "",
                    payments = listOf(
                        AdvatecPaymentItem(
                            paymentType = context.paymentType ?: "CASH",
                            paymentAmount = amountMajor,
                        ),
                    ),
                ),
            )

            val url = "http://$host:$port/api/v2/incoming"
            val jsonBody = json.encodeToString(AdvatecCustomerRequest.serializer(), request)

            AppLogger.i(
                TAG,
                "Fiscalization: submitting to Advatec (Pump=${transaction.pumpNumber}, " +
                    "Dose=$doseLitres L, CustIdType=$custIdType)",
            )

            // Submit Customer data to Advatec
            val submitResult = try {
                submitCustomerData(url, jsonBody)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                AppLogger.w(TAG, "Fiscalization submission failed: ${e.message}")
                return@withLock FiscalizationResult(
                    success = false,
                    errorMessage = "Submission failed: ${e.message}",
                )
            }

            if (!submitResult.success) {
                return@withLock FiscalizationResult(
                    success = false,
                    errorMessage = submitResult.errorMessage
                        ?: "Advatec returned HTTP ${submitResult.statusCode}",
                )
            }

            // AP-013: Suspend on channel receive instead of busy-wait polling
            AppLogger.d(TAG, "Fiscalization: waiting for receipt (timeout=${RECEIPT_TIMEOUT_MS}ms)")

            val receipt = withTimeoutOrNull(RECEIPT_TIMEOUT_MS) {
                receiptChannel.receive()
            }

            if (receipt != null) {
                AppLogger.i(
                    TAG,
                    "Fiscalization: receipt received — ReceiptCode=${receipt.receiptCode}, " +
                        "TxId=${receipt.transactionId}",
                )
                FiscalizationResult(
                    success = true,
                    receiptCode = receipt.receiptCode,
                    receiptVCodeUrl = receipt.receiptVCodeUrl,
                    totalTaxAmount = receipt.totalTaxAmount,
                )
            } else {
                AppLogger.w(TAG, "Fiscalization: timed out waiting for receipt (${RECEIPT_TIMEOUT_MS / 1000}s)")
                FiscalizationResult(
                    success = false,
                    errorMessage = "Timed out waiting for fiscal receipt (${RECEIPT_TIMEOUT_MS / 1000}s)",
                )
            }
        }
    }

    override suspend fun isAvailable(): Boolean {
        val host = config.advatecDeviceAddress ?: "127.0.0.1"
        val port = config.advatecDevicePort ?: DEFAULT_DEVICE_PORT
        return try {
            Socket().use { socket ->
                socket.connect(InetSocketAddress(host, port), HEARTBEAT_TIMEOUT_MS)
                true
            }
        } catch (_: Exception) {
            false
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private fun ensureInitialized() {
        if (initialized) return

        synchronized(this) {
            if (initialized) return

            // AF-021: Use separate port to avoid BindException when both adapter and
            // fiscalization service are active (Scenario C deployments).
            val port = config.advatecFiscalWebhookListenerPort ?: DEFAULT_FISCAL_WEBHOOK_LISTENER_PORT
            try {
                val listener = AdvatecWebhookListener(
                    listenPort = port,
                    siteCode = config.siteCode,
                    webhookToken = config.advatecWebhookToken,
                )

                if (listener.start()) {
                    webhookListener = listener

                    // AP-013: Use a coroutine instead of a daemon thread to drain
                    // webhook payloads. Sends parsed receipts to the Channel so
                    // submitForFiscalization can suspend-receive instead of busy-wait.
                    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
                    drainScope = scope
                    scope.launch {
                        while (isActive) {
                            try {
                                val payloads = listener.drainQueue(maxCount = 50)
                                for (payload in payloads) {
                                    try {
                                        val envelope = json.decodeFromString<AdvatecWebhookEnvelope>(payload.rawJson)
                                        if (envelope.dataType.equals("Receipt", ignoreCase = true) && envelope.data != null) {
                                            receiptChannel.send(envelope.data)
                                            AppLogger.d(TAG, "Fiscalization: receipt enqueued from webhook")
                                        }
                                    } catch (e: Exception) {
                                        AppLogger.w(TAG, "Fiscalization: failed to parse webhook: ${e.message}")
                                    }
                                }
                                delay(DRAIN_INTERVAL_MS)
                            } catch (_: CancellationException) {
                                break
                            } catch (e: Exception) {
                                AppLogger.w(TAG, "Fiscalization drain loop error: ${e.message}")
                            }
                        }
                    }

                    AppLogger.i(TAG, "Fiscalization webhook listener started on port $port")
                } else {
                    AppLogger.w(TAG, "Fiscalization webhook listener failed to start on port $port")
                }
            } catch (e: Exception) {
                AppLogger.e(TAG, "Fiscalization webhook listener init error: ${e.message}")
            }

            initialized = true
        }
    }

    override fun shutdown() {
        initialized = false
        drainScope?.cancel()
        drainScope = null
        webhookListener?.stop()
        webhookListener = null
        // Drain any remaining receipts from the channel
        while (receiptChannel.tryReceive().isSuccess) { /* drain */ }
    }

    // ── HTTP submission ─────────────────────────────────────────────────────

    private fun submitCustomerData(url: String, jsonBody: String): FiscalSubmitResult {
        val connection = (URL(url).openConnection() as HttpURLConnection).apply {
            requestMethod = "POST"
            connectTimeout = SUBMIT_TIMEOUT_MS
            readTimeout = SUBMIT_TIMEOUT_MS
            doOutput = true
            setRequestProperty("Content-Type", "application/json; charset=UTF-8")
        }

        return try {
            connection.outputStream.use { os ->
                OutputStreamWriter(os, Charsets.UTF_8).use { writer ->
                    writer.write(jsonBody)
                    writer.flush()
                }
            }

            val statusCode = connection.responseCode
            val responseBody = try {
                if (statusCode in 200..299) {
                    connection.inputStream.bufferedReader().readText()
                } else {
                    connection.errorStream?.bufferedReader()?.readText() ?: ""
                }
            } catch (_: Exception) {
                ""
            }

            AppLogger.i(TAG, "Customer submission response HTTP $statusCode: ${responseBody.take(500)}")

            FiscalSubmitResult(
                success = statusCode in 200..299,
                statusCode = statusCode,
                responseBody = responseBody,
            )
        } catch (e: java.net.SocketTimeoutException) {
            AppLogger.w(TAG, "Customer submission timed out after ${SUBMIT_TIMEOUT_MS}ms")
            FiscalSubmitResult(
                success = false,
                statusCode = 0,
                errorMessage = "Timeout after ${SUBMIT_TIMEOUT_MS}ms",
            )
        } finally {
            connection.disconnect()
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private fun getCurrencyFactor(currencyCode: String): BigDecimal {
        return when (currencyCode.uppercase()) {
            "KWD", "BHD", "OMR" -> BigDecimal("1000")
            "JPY", "KRW", "TZS", "UGX", "RWF" -> BigDecimal.ONE
            else -> BigDecimal("100")
        }
    }
}

/**
 * Internal result of Customer data HTTP submission.
 * Reuses the same structure as AdvatecAdapter's SubmitResult but is
 * package-private to avoid naming collisions.
 */
private data class FiscalSubmitResult(
    val success: Boolean,
    val statusCode: Int = 0,
    val responseBody: String? = null,
    val errorMessage: String? = null,
)

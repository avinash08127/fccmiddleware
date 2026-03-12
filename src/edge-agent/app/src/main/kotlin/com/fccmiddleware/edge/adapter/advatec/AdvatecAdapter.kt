package com.fccmiddleware.edge.adapter.advatec

import com.fccmiddleware.edge.adapter.common.*
import com.fccmiddleware.edge.logging.AppLogger
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.OutputStreamWriter
import java.math.BigDecimal
import java.math.RoundingMode
import java.net.HttpURLConnection
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URL
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlin.coroutines.cancellation.CancellationException

/**
 * Edge Kotlin adapter for Advatec TRA-compliant Electronic Fiscal Devices.
 *
 * Advatec is a fiscal device running on localhost:5560 that also triggers pump
 * authorization via Customer data submission (Scenario C). This adapter handles:
 *   1. Customer data submission → pump authorization (pre-auth)
 *   2. Receipt webhook JSON normalization to CanonicalTransaction
 *   3. Pre-auth ↔ Receipt correlation via customer data matching
 *   4. Heartbeat via TCP connect to the local Advatec device
 *   5. Webhook listener lifecycle (started on [ensureInitialized] call)
 *
 * Push-only: transactions arrive via [AdvatecWebhookListener] and are queued
 * internally. [fetchTransactions] drains that queue so the standard ingestion
 * pipeline buffers them.
 */
class AdvatecAdapter(private val config: AgentFccConfig) : IFccAdapter {

    companion object {
        private const val TAG = "AdvatecAdapter"
        private const val HEARTBEAT_TIMEOUT_MS = 5000
        private const val DEFAULT_WEBHOOK_LISTENER_PORT = 8091
        private const val DEFAULT_DEVICE_PORT = 5560
        private const val SUBMIT_TIMEOUT_MS = 10_000

        /** Maximum age for an active pre-auth before it's considered stale. */
        private const val PRE_AUTH_TTL_MILLIS = 30L * 60 * 1000 // 30 minutes

        private val MICROLITRES_PER_LITRE = BigDecimal("1000000")
        private val DAR_ES_SALAAM = ZoneId.of("Africa/Dar_es_Salaam")
        private val DATE_FMT = DateTimeFormatter.ofPattern("yyyy-MM-dd")
        private val TIME_FMT = DateTimeFormatter.ofPattern("HH:mm:ss")

        private val json = Json {
            ignoreUnknownKeys = true
            isLenient = true
        }
    }

    // ── Webhook listener (ADV-3.2) ────────────────────────────────────────────

    private var webhookListener: AdvatecWebhookListener? = null

    @Volatile
    private var initialized = false

    // ── Pre-auth tracking (ADV-4.3, ADV-4.4) ────────────────────────────────

    /**
     * Active pre-authorizations keyed by pump number. One active pre-auth per pump.
     * Populated by [sendPreAuth], consumed by receipt correlation in [normalizeReceipt].
     */
    private val activePreAuths = ConcurrentHashMap<Int, ActivePreAuth>()

    private fun ensureInitialized() {
        if (initialized) return

        synchronized(this) {
            if (initialized) return

            val port = config.advatecWebhookListenerPort ?: DEFAULT_WEBHOOK_LISTENER_PORT
            try {
                val listener = AdvatecWebhookListener(
                    listenPort = port,
                    siteCode = config.siteCode,
                    webhookToken = config.advatecWebhookToken,
                )
                val started = listener.start()
                if (started) {
                    webhookListener = listener
                    AppLogger.i(TAG, "Webhook listener started on port $port")
                } else {
                    AppLogger.w(TAG, "Webhook listener failed to start on port $port (non-fatal)")
                }
            } catch (e: Exception) {
                AppLogger.e(TAG, "Webhook listener init error on port $port: ${e.message} (non-fatal)")
            }

            initialized = true
        }
    }

    fun shutdown() {
        webhookListener?.stop()
        webhookListener = null
        initialized = false
    }

    val isWebhookListening: Boolean
        get() = webhookListener?.isRunning == true

    val webhookQueueSize: Int
        get() = webhookListener?.queueSize ?: 0

    val activePreAuthCount: Int
        get() = activePreAuths.size

    // ── normalize ────────────────────────────────────────────────────────────

    override suspend fun normalize(rawPayload: RawPayloadEnvelope): NormalizationResult {
        return try {
            val envelope = try {
                json.decodeFromString<AdvatecWebhookEnvelope>(rawPayload.payload)
            } catch (e: Exception) {
                return NormalizationResult.Failure(
                    errorCode = "INVALID_PAYLOAD",
                    message = "Failed to parse Advatec webhook JSON: ${e.message}",
                )
            }

            if (!envelope.dataType.equals("Receipt", ignoreCase = true)) {
                return NormalizationResult.Failure(
                    errorCode = "UNSUPPORTED_MESSAGE_TYPE",
                    message = "Advatec DataType '${envelope.dataType}' is not 'Receipt'",
                )
            }

            val receipt = envelope.data
                ?: return NormalizationResult.Failure(
                    errorCode = "MISSING_REQUIRED_FIELD",
                    message = "Advatec webhook payload has no Data",
                    fieldName = "Data",
                )

            // Purge stale pre-auths on each normalization cycle
            purgeStalePreAuths()

            normalizeReceipt(receipt, rawPayload)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            NormalizationResult.Failure(
                errorCode = "INVALID_PAYLOAD",
                message = "Normalization error: ${e::class.simpleName}: ${e.message}",
            )
        }
    }

    private fun normalizeReceipt(
        receipt: AdvatecReceiptData,
        rawPayload: RawPayloadEnvelope,
    ): NormalizationResult {
        // --- Validate required fields ---
        if (receipt.transactionId.isNullOrBlank()) {
            return NormalizationResult.Failure(
                errorCode = "MISSING_REQUIRED_FIELD",
                message = "Receipt missing TransactionId",
                fieldName = "TransactionId",
            )
        }

        val items = receipt.items
        if (items.isNullOrEmpty()) {
            return NormalizationResult.Failure(
                errorCode = "MISSING_REQUIRED_FIELD",
                message = "Receipt has no Items",
                fieldName = "Items",
            )
        }

        val item = items[0]

        // --- Volume: Quantity (BigDecimal litres) -> microlitres ---
        val volumeMicrolitres = item.quantity
            .multiply(MICROLITRES_PER_LITRE)
            .setScale(0, RoundingMode.HALF_UP)
            .toLong()

        // --- Amount & price: BigDecimal conversion via currency factor ---
        val currencyFactor = getCurrencyFactor(config.currencyCode)
        val amountMinorUnits = receipt.amountInclusive
            .multiply(currencyFactor)
            .setScale(0, RoundingMode.HALF_UP)
            .toLong()

        val unitPriceMinorPerLitre = item.price
            .multiply(currencyFactor)
            .setScale(0, RoundingMode.HALF_UP)
            .toLong()

        // --- Sanity check: price * quantity ~= amount (within discount tolerance) ---
        val expectedAmount = item.price.multiply(item.quantity)
        val discount = item.discountAmount ?: BigDecimal.ZERO
        val diff = expectedAmount.subtract(item.amount).subtract(discount).abs()
        if (diff.compareTo(BigDecimal.ONE) > 0) {
            AppLogger.w(
                TAG,
                "Sanity check: Price(${item.price}) * Qty(${item.quantity}) = $expectedAmount " +
                    "but Item.Amount=${item.amount}, Discount=$discount, diff=$diff",
            )
        }

        // --- Product code mapping ---
        val rawProduct = item.product ?: "UNKNOWN"
        val productCode = config.productCodeMapping[rawProduct] ?: rawProduct

        // --- Timestamps: Date + Time with configured timezone -> UTC ---
        val timezone = try {
            ZoneId.of(config.timezone)
        } catch (_: Exception) {
            DAR_ES_SALAAM
        }

        val completedAt = parseAdvatecTimestamp(receipt.date, receipt.time, timezone)
            ?: rawPayload.receivedAtUtc
        val startedAt = completedAt // Only one timestamp available from Advatec

        // --- Dedup key: {siteCode}-{TransactionId} ---
        val fccTransactionId = "${rawPayload.siteCode}-${receipt.transactionId}"

        // --- Pre-auth correlation (ADV-4.4) ---
        val matchedPreAuth = tryMatchPreAuth(receipt)

        // Pump number: from pre-auth if matched, otherwise config default (AQ-3)
        val pumpNumber = matchedPreAuth?.pumpNumber ?: (0 + config.pumpNumberOffset)

        val correlationId = matchedPreAuth?.correlationId ?: UUID.randomUUID().toString()
        val odooOrderId = matchedPreAuth?.odooOrderId
        val preAuthId = matchedPreAuth?.preAuthId

        if (matchedPreAuth == null && !receipt.customerId.isNullOrBlank()) {
            AppLogger.d(
                TAG,
                "Receipt has CustomerId=${receipt.customerId} but no matching pre-auth — Normal Order",
            )
        }

        val now = Instant.now().toString()

        return NormalizationResult.Success(
            CanonicalTransaction(
                id = UUID.randomUUID().toString(),
                fccTransactionId = fccTransactionId,
                siteCode = rawPayload.siteCode,
                pumpNumber = pumpNumber,
                nozzleNumber = 1, // Advatec has no nozzle concept (AQ-9)
                productCode = productCode,
                volumeMicrolitres = volumeMicrolitres,
                amountMinorUnits = amountMinorUnits,
                unitPriceMinorPerLitre = unitPriceMinorPerLitre,
                startedAt = startedAt,
                completedAt = completedAt,
                fccVendor = FccVendor.ADVATEC,
                legalEntityId = rawPayload.siteCode,
                currencyCode = config.currencyCode,
                status = TransactionStatus.PENDING,
                ingestionSource = IngestionSource.FCC_PUSH,
                ingestedAt = now,
                updatedAt = now,
                schemaVersion = 1,
                isDuplicate = false,
                correlationId = correlationId,
                odooOrderId = odooOrderId,
                preAuthId = preAuthId,
                fiscalReceiptNumber = receipt.receiptCode,
                rawPayloadJson = rawPayload.payload,
            ),
        )
    }

    // ── fetchTransactions (drain webhook queue) ───────────────────────────────

    override suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch {
        ensureInitialized()

        val listener = webhookListener ?: return TransactionBatch(
            transactions = emptyList(),
            hasMore = false,
        )

        val drained = listener.drainQueue(maxCount = 200)
        if (drained.isEmpty()) {
            return TransactionBatch(
                transactions = emptyList(),
                hasMore = false,
            )
        }

        // Normalize each webhook payload into CanonicalTransaction (same pattern as RadixAdapter)
        val transactions = mutableListOf<CanonicalTransaction>()
        for (payload in drained) {
            try {
                val rawEnvelope = RawPayloadEnvelope(
                    vendor = FccVendor.ADVATEC,
                    siteCode = payload.siteCode,
                    receivedAtUtc = payload.receivedAt.toString(),
                    contentType = "application/json",
                    payload = payload.rawJson,
                )
                when (val normResult = normalize(rawEnvelope)) {
                    is NormalizationResult.Success -> {
                        transactions.add(
                            normResult.transaction.copy(
                                ingestionSource = IngestionSource.FCC_PUSH,
                            ),
                        )
                    }
                    is NormalizationResult.Failure -> {
                        AppLogger.w(TAG, "fetchTransactions: normalization failed: ${normResult.errorCode} — ${normResult.message}")
                    }
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                AppLogger.w(TAG, "fetchTransactions: error processing webhook payload: ${e.message}")
            }
        }

        return TransactionBatch(
            transactions = transactions,
            hasMore = listener.queueSize > 0,
        )
    }

    // ── acknowledgeTransactions ──────────────────────────────────────────────

    override suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean {
        return true
    }

    // ── getPumpStatus ────────────────────────────────────────────────────────

    override suspend fun getPumpStatus(): List<PumpStatus> {
        return emptyList()
    }

    // ── sendPreAuth (ADV-4.3) ────────────────────────────────────────────────

    /**
     * Submits Customer data to Advatec to trigger pump authorization.
     * Maps [PreAuthCommand] fields to [AdvatecCustomerRequest],
     * stores correlation entry for receipt matching.
     */
    override suspend fun sendPreAuth(command: PreAuthCommand): PreAuthResult {
        val host = config.advatecDeviceAddress ?: "127.0.0.1"
        val port = config.advatecDevicePort ?: DEFAULT_DEVICE_PORT

        // Dose = requested amount / unit price → litres
        val doseLitres = if (command.unitPrice > 0) {
            BigDecimal(command.amountMinorUnits)
                .divide(BigDecimal(command.unitPrice), 4, RoundingMode.HALF_UP)
        } else {
            BigDecimal.ZERO
        }

        val custIdType = command.customerIdType
            ?: config.advatecCustIdType
            ?: 6 // 6 = NIL (TRA default)

        val request = AdvatecCustomerRequest(
            dataType = "Customer",
            data = AdvatecCustomerData(
                pump = command.pumpNumber,
                dose = doseLitres,
                custIdType = custIdType,
                customerId = command.customerTaxId ?: "",
                customerName = command.customerName ?: "",
                payments = emptyList(), // Empty during pre-auth per AQ-5
            ),
        )

        val url = "http://$host:$port/api/v2/incoming"
        val jsonBody = json.encodeToString(request)

        AppLogger.i(
            TAG,
            "Submitting Customer data to $url (Pump=${command.pumpNumber}, " +
                "Dose=$doseLitres L, CustIdType=$custIdType)",
        )

        val submitResult = try {
            submitCustomerData(url, jsonBody)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            AppLogger.w(TAG, "Customer submission failed: ${e.message}")
            return PreAuthResult(
                status = PreAuthResultStatus.TIMEOUT,
                message = "Advatec submission failed: ${e.message}",
            )
        }

        if (!submitResult.success) {
            val status = if (submitResult.statusCode == 0) {
                PreAuthResultStatus.TIMEOUT
            } else {
                PreAuthResultStatus.ERROR
            }
            return PreAuthResult(
                status = status,
                message = submitResult.errorMessage
                    ?: "Advatec returned HTTP ${submitResult.statusCode}: ${submitResult.responseBody}",
            )
        }

        // Store active pre-auth for receipt correlation (ADV-4.4)
        val correlationId = "ADV-${command.pumpNumber}-${System.currentTimeMillis()}"
        val activePreAuth = ActivePreAuth(
            pumpNumber = command.pumpNumber,
            correlationId = correlationId,
            odooOrderId = command.odooOrderId,
            preAuthId = null,
            customerId = command.customerTaxId,
            customerName = command.customerName,
            doseLitres = doseLitres,
            createdAtMillis = System.currentTimeMillis(),
        )

        // One pre-auth per pump — overwrite any stale entry
        activePreAuths[command.pumpNumber] = activePreAuth

        AppLogger.i(
            TAG,
            "Pre-auth stored: Pump=${command.pumpNumber}, CorrelationId=$correlationId, " +
                "Dose=$doseLitres L, OdooOrderId=${command.odooOrderId}",
        )

        return PreAuthResult(
            status = PreAuthResultStatus.AUTHORIZED,
            correlationId = correlationId,
            message = "Customer data submitted to Advatec",
        )
    }

    // ── cancelPreAuth (ADV-4.3) ──────────────────────────────────────────────

    /**
     * Removes the active pre-auth entry. Advatec has no documented cancel API,
     * but we clean up the correlation map so the receipt won't be matched.
     */
    override suspend fun cancelPreAuth(command: CancelPreAuthCommand): Boolean {
        val correlationId = command.fccCorrelationId ?: return false

        val iterator = activePreAuths.entries.iterator()
        while (iterator.hasNext()) {
            val entry = iterator.next()
            if (entry.value.correlationId == correlationId) {
                iterator.remove()
                AppLogger.i(
                    TAG,
                    "Pre-auth cancelled: CorrelationId=$correlationId, Pump=${entry.value.pumpNumber}",
                )
                return true
            }
        }

        AppLogger.d(TAG, "Cancel pre-auth: no active pre-auth found for $correlationId")
        return false
    }

    // ── heartbeat ────────────────────────────────────────────────────────────

    override suspend fun heartbeat(): Boolean {
        val host = config.advatecDeviceAddress ?: "127.0.0.1"
        val port = config.advatecDevicePort ?: DEFAULT_DEVICE_PORT
        return try {
            Socket().use { socket ->
                socket.connect(InetSocketAddress(host, port), HEARTBEAT_TIMEOUT_MS)
                true
            }
        } catch (e: Exception) {
            AppLogger.d(TAG, "Heartbeat failed for Advatec at $host:$port — ${e.message}")
            false
        }
    }

    // ── Private: HTTP submission ─────────────────────────────────────────────

    private fun submitCustomerData(url: String, jsonBody: String): SubmitResult {
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

            SubmitResult(
                success = statusCode in 200..299,
                statusCode = statusCode,
                responseBody = responseBody,
            )
        } catch (e: java.net.SocketTimeoutException) {
            AppLogger.w(TAG, "Customer submission timed out after ${SUBMIT_TIMEOUT_MS}ms")
            SubmitResult(
                success = false,
                statusCode = 0,
                errorMessage = "Timeout after ${SUBMIT_TIMEOUT_MS}ms",
            )
        } finally {
            connection.disconnect()
        }
    }

    // ── Private: pre-auth ↔ receipt correlation (ADV-4.4) ───────────────────

    /**
     * Attempts to find a matching active pre-auth for an incoming receipt.
     * Strategy:
     *   1. Match by CustomerId if both the receipt and a pre-auth have one.
     *   2. Fallback: match the oldest active pre-auth within the TTL window (FIFO).
     * Returns null if no match found (Normal Order).
     */
    private fun tryMatchPreAuth(receipt: AdvatecReceiptData): ActivePreAuth? {
        if (activePreAuths.isEmpty()) return null

        val now = System.currentTimeMillis()

        // Strategy 1: Match by CustomerId (receipt echoes back the customer data we submitted)
        if (!receipt.customerId.isNullOrBlank()) {
            val iterator = activePreAuths.entries.iterator()
            while (iterator.hasNext()) {
                val entry = iterator.next()
                val preAuth = entry.value
                if (preAuth.customerId.equals(receipt.customerId, ignoreCase = true)
                    && (now - preAuth.createdAtMillis) < PRE_AUTH_TTL_MILLIS
                ) {
                    iterator.remove()
                    AppLogger.i(
                        TAG,
                        "Receipt correlated by CustomerId: Pump=${preAuth.pumpNumber}, " +
                            "CorrelationId=${preAuth.correlationId}, OdooOrderId=${preAuth.odooOrderId}",
                    )
                    return preAuth
                }
            }
        }

        // Strategy 2: FIFO — oldest active pre-auth within TTL
        // Advatec processes requests sequentially on a single device, so the oldest
        // pending pre-auth is the most likely match for the next receipt.
        var oldest: ActivePreAuth? = null
        var oldestKey: Int? = null
        for (entry in activePreAuths) {
            val preAuth = entry.value
            if ((now - preAuth.createdAtMillis) >= PRE_AUTH_TTL_MILLIS) continue

            if (oldest == null || preAuth.createdAtMillis < oldest.createdAtMillis) {
                oldest = preAuth
                oldestKey = entry.key
            }
        }

        if (oldest != null && oldestKey != null) {
            activePreAuths.remove(oldestKey)
            AppLogger.i(
                TAG,
                "Receipt correlated by FIFO: Pump=${oldest.pumpNumber}, " +
                    "CorrelationId=${oldest.correlationId}, OdooOrderId=${oldest.odooOrderId}",
            )
        }

        return oldest
    }

    /**
     * Removes pre-auth entries older than [PRE_AUTH_TTL_MILLIS].
     * Called during normalization to prevent memory leaks.
     */
    private fun purgeStalePreAuths() {
        val now = System.currentTimeMillis()
        val iterator = activePreAuths.entries.iterator()
        while (iterator.hasNext()) {
            val entry = iterator.next()
            val ageMillis = now - entry.value.createdAtMillis
            if (ageMillis >= PRE_AUTH_TTL_MILLIS) {
                iterator.remove()
                AppLogger.w(
                    TAG,
                    "Stale pre-auth purged: Pump=${entry.value.pumpNumber}, " +
                        "CorrelationId=${entry.value.correlationId}, Age=${ageMillis / 60000}min",
                )
            }
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private fun parseAdvatecTimestamp(date: String?, time: String?, timezone: ZoneId): String? {
        if (date.isNullOrBlank()) return null
        return try {
            val localDate = LocalDate.parse(date, DATE_FMT)
            val localTime = if (!time.isNullOrBlank()) {
                LocalTime.parse(time, TIME_FMT)
            } else {
                LocalTime.MIDNIGHT
            }
            ZonedDateTime.of(localDate, localTime, timezone)
                .toInstant()
                .toString()
        } catch (e: Exception) {
            AppLogger.d(TAG, "Failed to parse Advatec timestamp date='$date' time='$time': ${e.message}")
            null
        }
    }

    private fun getCurrencyFactor(currencyCode: String): BigDecimal {
        return when (currencyCode.uppercase()) {
            "KWD", "BHD", "OMR" -> BigDecimal("1000")
            "JPY", "KRW", "TZS", "UGX", "RWF" -> BigDecimal.ONE
            else -> BigDecimal("100")
        }
    }
}

/**
 * Tracks an in-flight pre-authorization awaiting its Receipt webhook.
 * Keyed by pump number in [AdvatecAdapter.activePreAuths].
 */
data class ActivePreAuth(
    val pumpNumber: Int,
    val correlationId: String,
    val odooOrderId: String?,
    val preAuthId: String?,
    val customerId: String?,
    val customerName: String?,
    val doseLitres: BigDecimal,
    val createdAtMillis: Long,
)

/**
 * Result of submitting Customer data to the Advatec device.
 */
private data class SubmitResult(
    val success: Boolean,
    val statusCode: Int = 0,
    val responseBody: String? = null,
    val errorMessage: String? = null,
)

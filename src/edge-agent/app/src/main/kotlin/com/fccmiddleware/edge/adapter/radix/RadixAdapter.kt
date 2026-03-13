package com.fccmiddleware.edge.adapter.radix

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.*
import com.fccmiddleware.edge.adapter.common.IPreAuthMatcher
import com.fccmiddleware.edge.adapter.common.PreAuthMatchResult
import com.fccmiddleware.edge.adapter.common.PreAuthMatchingStrategy
import com.fccmiddleware.edge.adapter.common.ActivePreAuthSnapshot
import com.fccmiddleware.edge.adapter.common.PumpStatusCapability
import io.ktor.client.HttpClient
import io.ktor.client.engine.okhttp.OkHttp
import io.ktor.client.request.header
import io.ktor.client.request.post
import io.ktor.client.request.setBody
import io.ktor.client.statement.bodyAsText
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout
import java.math.BigDecimal
import java.math.RoundingMode
import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.UUID
import java.io.Closeable
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/**
 * RadixAdapter — Edge Agent adapter for the Radix FCC protocol.
 *
 * Communicates with the FCC over station LAN using HTTP POST with XML bodies:
 *   Auth port      : P (from config authPort) — external authorization (pre-auth)
 *   Transaction port: P+1 — transaction management, products, day close, ATG, CSR
 *   Signing        : SHA-1 hash of XML body + shared secret password
 *   Heartbeat      : CMD_CODE=55 (product/price read) — no dedicated endpoint
 *   Fetch          : FIFO drain loop: CMD_CODE=10 (request) -> CMD_CODE=201 (ACK) -> repeat
 *   Pre-auth       : <AUTH_DATA> XML to auth port P
 *   Pump status    : Not supported by Radix protocol
 *
 * Implements Phase 2 tasks:
 *   RX-3.1 — Transaction fetch (FIFO drain)
 *   RX-3.3 — Normalization (volume/amount via BigDecimal, timestamps, dedup key)
 *   RX-3.5 — Mode management (ON_DEMAND caching)
 *   RX-4.1 — Pre-auth (AUTH_DATA to auth port with ACKCODE mapping)
 *   RX-4.3 — TOKEN correlation (pre-auth -> transaction matching)
 *   RX-5.1 — Unsolicited push listener (PUSH/HYBRID ingestion via RadixPushListener)
 */
class RadixAdapter(
    private val config: AgentFccConfig,
    private val httpClient: HttpClient = HttpClient(OkHttp),
) : IFccAdapter, Closeable {

    override val pumpStatusCapability = PumpStatusCapability.SYNTHESIZED

    override val preAuthMatcher: IPreAuthMatcher = object : IPreAuthMatcher {
        override val matchingStrategy = PreAuthMatchingStrategy.DETERMINISTIC

        override fun registerPreAuth(command: PreAuthCommand, vendorRef: String?): String {
            return "RADIX-TOKEN-${vendorRef ?: "0"}"
        }

        override fun matchTransaction(pumpNumber: Int, vendorMatchKey: String?): PreAuthMatchResult? {
            if (vendorMatchKey == null) return null
            val token = vendorMatchKey.toIntOrNull() ?: return null
            val preAuth = activePreAuths[token] ?: return null
            return PreAuthMatchResult(
                correlationId = "RADIX-TOKEN-$token",
                strategy = PreAuthMatchingStrategy.DETERMINISTIC,
                odooOrderId = preAuth.odooOrderId,
            )
        }

        override fun removePreAuth(correlationId: String): Boolean {
            val token = correlationId.removePrefix("RADIX-TOKEN-").toIntOrNull() ?: return false
            return activePreAuths.remove(token) != null
        }

        override fun getActivePreAuths(): List<ActivePreAuthSnapshot> {
            return activePreAuths.entries.map { (token, preAuth) ->
                ActivePreAuthSnapshot(
                    correlationId = "RADIX-TOKEN-$token",
                    pumpNumber = preAuth.pumpNumber,
                    registeredAtUtc = preAuth.createdAt.toString(),
                    odooOrderId = preAuth.odooOrderId,
                )
            }
        }

        override fun purgeStale(): Int {
            val cutoff = Instant.now().minusSeconds(PREAUTH_TTL_MINUTES * 60)
            var purged = 0
            val iterator = activePreAuths.entries.iterator()
            while (iterator.hasNext()) {
                val entry = iterator.next()
                if (entry.value.createdAt.isBefore(cutoff)) {
                    iterator.remove()
                    purged++
                    AppLogger.d(TAG, "IPreAuthMatcher.purgeStale: token=${entry.key}, age > ${PREAUTH_TTL_MINUTES}m")
                }
            }
            return purged
        }
    }

    // -----------------------------------------------------------------------
    // Token counter — shared across heartbeat, fetch, and pre-auth
    // -----------------------------------------------------------------------

    /** Sequential token counter (1-65535, wraps at 65536). Thread-safe. Starts at 1 because TOKEN=0 means "Normal Order" (no pre-auth) in the Radix protocol. */
    private val tokenCounter = AtomicInteger(1)

    /** Generates the next sequential token (1-65535), wrapping around and skipping 0. Thread-safe. */
    private fun nextToken(): Int {
        return tokenCounter.getAndUpdate { val next = (it + 1) % TOKEN_WRAP; if (next == 0) 1 else next }
    }

    // -----------------------------------------------------------------------
    // Mode management (RX-3.5)
    // -----------------------------------------------------------------------

    /**
     * Cached current FCC transaction transfer mode.
     *   -1 = unknown (not yet set or reset after connectivity loss)
     *    0 = OFF (transaction transfer disabled)
     *    1 = ON_DEMAND (pull mode)
     *    2 = UNSOLICITED (push mode)
     */
    @Volatile
    private var currentMode: Int = MODE_UNKNOWN

    /**
     * Sends CMD_CODE=20 to set the transaction transfer mode, but only if
     * the cached mode differs from the requested mode.
     *
     * @param mode Target mode (0=ON_DEMAND, 1=OFF, 2=UNSOLICITED)
     * @return true if mode is confirmed set (either already cached or request succeeded)
     */
    private suspend fun ensureMode(mode: Int): Boolean {
        if (currentMode == mode) return true

        return try {
            val token = nextToken().toString()
            val requestBody = RadixXmlBuilder.buildModeChangeRequest(mode, token, sharedSecret)
            val headers = RadixXmlBuilder.buildHttpHeaders(usnCode, RadixXmlBuilder.OPERATION_TRANSACTION)
            val url = "http://${config.hostAddress}:$transactionPort"

            val response = httpClient.post(url) {
                headers.forEach { (key, value) -> header(key, value) }
                setBody(requestBody)
            }

            val responseBody = response.bodyAsText()
            val parseResult = RadixXmlParser.parseTransactionResponse(responseBody)

            when (parseResult) {
                is RadixParseResult.Success -> {
                    if (parseResult.value.respCode == RESP_CODE_SUCCESS) {
                        currentMode = mode
                        AppLogger.d(TAG, "Mode changed to $mode")
                        true
                    } else {
                        AppLogger.w(TAG, "Mode change failed: RESP_CODE=${parseResult.value.respCode}")
                        false
                    }
                }
                is RadixParseResult.Error -> {
                    AppLogger.w(TAG, "Mode change parse error: ${parseResult.message}")
                    false
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            AppLogger.w(TAG, "Mode change failed: ${e::class.simpleName}: ${e.message}")
            resetModeState()
            false
        }
    }

    /** Reset cached mode on connectivity loss. */
    private fun resetModeState() {
        currentMode = MODE_UNKNOWN
    }

    // -----------------------------------------------------------------------
    // Push listener (RX-5.1)
    // -----------------------------------------------------------------------

    /**
     * Unsolicited push listener for PUSH/HYBRID ingestion modes.
     *
     * When non-null, the FDC is configured to push transactions to this
     * listener's HTTP endpoint instead of (or in addition to) the agent polling.
     * Lazily created on first fetchTransactions() call when ingestion mode
     * is PUSH or HYBRID.
     */
    @Volatile
    var radixPushListener: RadixPushListener? = null
        private set

    /** Default port for the push listener. Configurable via pushListenerPort. */
    private val pushListenerPort: Int
        get() = (config.authPort ?: config.port) + 2

    /**
     * Starts the push listener and sets FDC to UNSOLICITED mode.
     *
     * @return true if both the listener started and the mode was set successfully
     */
    private suspend fun ensurePushListenerRunning(): Boolean {
        // Create listener if not yet initialized
        if (radixPushListener == null) {
            radixPushListener = RadixPushListener(
                listenPort = pushListenerPort,
                expectedUsnCode = usnCode,
                sharedSecret = sharedSecret,
            )
        }

        val listener = radixPushListener!!

        // Start the HTTP server if not already running
        if (!listener.isRunning) {
            if (!listener.start()) {
                AppLogger.w(TAG, "Failed to start push listener on port $pushListenerPort")
                return false
            }
        }

        // Set FDC to UNSOLICITED mode
        if (!ensureMode(MODE_UNSOLICITED)) {
            AppLogger.w(TAG, "Failed to set UNSOLICITED mode on FDC")
            return false
        }

        return true
    }

    /**
     * Stops the push listener and resets mode state.
     *
     * Called when the adapter is being shut down or when switching back to pull mode.
     */
    fun stopPushListener() {
        radixPushListener?.stop()
        radixPushListener = null
        resetModeState()
    }

    /**
     * Releases all resources held by this adapter: stops the push listener
     * and closes the HTTP client (connection pool + dispatcher threads).
     *
     * Must be called when the adapter is replaced or the service shuts down
     * to prevent thread/connection leaks on Android.
     */
    override fun close() {
        stopPushListener()
        try {
            httpClient.close()
        } catch (e: Exception) {
            AppLogger.w(TAG, "Error closing HttpClient: ${e.message}")
        }
    }

    /**
     * Collects transactions pushed by the FDC via the [RadixPushListener].
     *
     * Drains the listener's queue, wraps each raw XML payload in a
     * [RawPayloadEnvelope], normalizes it, and returns the batch.
     *
     * @param limit Maximum number of transactions to collect
     * @return List of normalized [CanonicalTransaction] from pushed payloads
     */
    private suspend fun collectPushedTransactions(limit: Int): List<CanonicalTransaction> {
        val listener = radixPushListener ?: return emptyList()
        val pushed = listener.drainQueue(limit)
        if (pushed.isEmpty()) return emptyList()

        val transactions = mutableListOf<CanonicalTransaction>()
        for (item in pushed) {
            try {
                val rawEnvelope = RawPayloadEnvelope(
                    vendor = FccVendor.RADIX,
                    siteCode = config.siteCode,
                    receivedAtUtc = item.receivedAt.toString(),
                    contentType = "text/xml",
                    payload = item.rawXml,
                )

                when (val normResult = normalize(rawEnvelope)) {
                    is NormalizationResult.Success -> {
                        // Override ingestion source to FCC_PUSH for pushed transactions
                        transactions.add(
                            normResult.transaction.copy(
                                ingestionSource = IngestionSource.FCC_PUSH,
                            )
                        )
                    }
                    is NormalizationResult.Failure -> {
                        AppLogger.w(TAG, "collectPushedTransactions: normalization failed: ${normResult.errorCode} — ${normResult.message}")
                    }
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                AppLogger.w(TAG, "collectPushedTransactions: error processing pushed transaction: ${e.message}")
            }
        }

        return transactions
    }

    // -----------------------------------------------------------------------
    // Pre-auth tracking (RX-4.1, RX-4.3)
    // -----------------------------------------------------------------------

    /** Active pre-auth entries keyed by TOKEN for later transaction correlation. */
    private val activePreAuths = ConcurrentHashMap<Int, PreAuthEntry>()

    /**
     * Purges pre-auth entries older than [PREAUTH_TTL_MINUTES] to prevent memory leaks.
     * Called during each fetch cycle. Entries can become stale when the FCC goes offline,
     * the customer walks away, or a dispense never occurs.
     */
    private fun purgeStalePreAuths() {
        val cutoff = Instant.now().minusSeconds(PREAUTH_TTL_MINUTES * 60)
        val iterator = activePreAuths.entries.iterator()
        while (iterator.hasNext()) {
            val entry = iterator.next()
            if (entry.value.createdAt.isBefore(cutoff)) {
                iterator.remove()
                AppLogger.d(TAG, "Purged stale pre-auth: token=${entry.key}, age > ${PREAUTH_TTL_MINUTES}m")
            }
        }
    }

    // -----------------------------------------------------------------------
    // Pump address mapping
    // -----------------------------------------------------------------------

    /**
     * Parsed pump address map: canonical pump number -> (PUMP_ADDR, FP).
     * Lazily parsed from config.fccPumpAddressMap JSON string.
     * Format: { "1": {"pumpAddr": 0, "fp": 0}, "2": {"pumpAddr": 0, "fp": 1}, ... }
     */
    private val pumpAddressMap: Map<Int, PumpAddressEntry> by lazy {
        parsePumpAddressMap(config.fccPumpAddressMap)
    }

    /**
     * Reverse pump address map: (PUMP_ADDR, FP) -> canonical pump number.
     * Used during normalization to resolve FCC-native addresses back to canonical numbers.
     */
    private val reversePumpAddressMap: Map<String, Int> by lazy {
        pumpAddressMap.entries.associate { (pumpNumber, entry) ->
            "${entry.pumpAddr}-${entry.fp}" to pumpNumber
        }
    }

    // -----------------------------------------------------------------------
    // Derived config helpers
    // -----------------------------------------------------------------------

    /** Transaction management port = authPort + 1. Falls back to config.port + 1 if authPort not set. */
    private val transactionPort: Int
        get() = (config.authPort ?: config.port) + 1

    /** Authorization port = authPort. Falls back to config.port if authPort not set. */
    private val authPort: Int
        get() = config.authPort ?: config.port

    private val sharedSecret: String
        get() = config.sharedSecret ?: ""

    private val usnCode: Int
        get() = config.usnCode ?: 0

    private val siteTimezone: ZoneId by lazy {
        try {
            ZoneId.of(config.timezone)
        } catch (e: Exception) {
            AppLogger.w(TAG, "Invalid timezone '${config.timezone}', falling back to UTC")
            ZoneId.of("UTC")
        }
    }

    /** Currency decimal factor for converting amount strings to minor units. */
    private val currencyDecimalFactor: BigDecimal by lazy {
        // Most currencies use 2 decimal places (factor 100).
        // TZS and similar 0-decimal currencies still use factor 100 in Radix
        // because Radix reports amounts as decimal strings.
        BigDecimal(100)
    }

    // -----------------------------------------------------------------------
    // IFccAdapter — heartbeat
    // -----------------------------------------------------------------------

    /**
     * Liveness probe using CMD_CODE=55 (product/price read) on port P+1.
     *
     * Returns true only when the FCC responds with RESP_CODE=201.
     * Never throws — returns false on any error (network, timeout, parse failure, signature).
     * Enforces a 5-second hard timeout.
     * Signature errors (RESP_CODE=251) are logged as warnings (config issue, not transient).
     */
    override suspend fun heartbeat(): Boolean {
        purgeStalePreAuths()
        return try {
            withTimeout(HEARTBEAT_TIMEOUT_MS) {
                val token = nextToken().toString()
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
                            AppLogger.w(TAG, "Heartbeat: signature error (RESP_CODE=251) — check sharedSecret configuration")
                            false
                        } else {
                            parseResult.value.respCode == RESP_CODE_SUCCESS
                        }
                    }
                    is RadixParseResult.Error -> {
                        AppLogger.w(TAG, "Heartbeat: failed to parse response: ${parseResult.message}")
                        false
                    }
                }
            }
        } catch (e: TimeoutCancellationException) {
            AppLogger.d(TAG, "Heartbeat: timeout after ${HEARTBEAT_TIMEOUT_MS}ms")
            false
        } catch (e: CancellationException) {
            throw e // Preserve structured concurrency cancellation
        } catch (e: Exception) {
            AppLogger.d(TAG, "Heartbeat: ${e::class.simpleName}: ${e.message}")
            resetModeState()
            false
        }
    }

    // -----------------------------------------------------------------------
    // IFccAdapter — fetchTransactions (RX-3.1)
    // -----------------------------------------------------------------------

    /**
     * Fetches transactions from the Radix FCC.
     *
     * Supports three ingestion strategies based on config:
     *
     * **PULL (default / CLOUD_DIRECT / RELAY / BUFFER_ALWAYS):**
     * 1. Ensure ON_DEMAND mode (CMD_CODE=20, MODE=0)
     * 2. FIFO drain loop: CMD_CODE=10 -> parse -> ACK CMD_CODE=201 -> repeat
     * 3. RESP_CODE=205: FIFO empty -> break
     *
     * **PUSH (config.ingestionMode indicates push-capable):**
     * 1. Start [RadixPushListener] on port P+2
     * 2. Set FDC to UNSOLICITED mode (CMD_CODE=20, MODE=2)
     * 3. Drain the push listener's queue of received transactions
     *
     * **HYBRID (both push and pull):**
     * 1. Start push listener and set UNSOLICITED mode
     * 2. Drain push queue first
     * 3. If under limit, also FIFO drain any remaining transactions in pull mode
     *
     * ACK is sent inline during the pull fetch loop — acknowledgeTransactions() is a no-op.
     * Push transactions are ACKed by the listener's HTTP response.
     */
    override suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch {
        purgeStalePreAuths()

        val limit = cursor.limit.coerceIn(1, MAX_FETCH_LIMIT)
        val transactions = mutableListOf<CanonicalTransaction>()

        try {
            // Determine ingestion strategy from config — aligned with .NET adapter
            // which checks ConnectionProtocol == "PUSH"
            val isPushCapable = config.connectionProtocol.equals("PUSH", ignoreCase = true)

            if (isPushCapable) {
                // PUSH/HYBRID mode — start listener and set UNSOLICITED mode
                if (!ensurePushListenerRunning()) {
                    AppLogger.w(TAG, "fetchTransactions: push listener startup failed, falling back to pull")
                    return fetchTransactionsPull(limit)
                }

                // Drain pushed transactions
                val pushed = collectPushedTransactions(limit)
                transactions.addAll(pushed)

                val hasMore = radixPushListener?.queueSize?.let { it > 0 } ?: false
                return TransactionBatch(
                    transactions = transactions,
                    hasMore = hasMore,
                )
            }

            // Standard PULL mode
            return fetchTransactionsPull(limit)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            AppLogger.w(TAG, "fetchTransactions: ${e::class.simpleName}: ${e.message}")
            resetModeState()
            return TransactionBatch(
                transactions = transactions,
                hasMore = false,
            )
        }
    }

    /**
     * Pull-mode transaction fetch via FIFO drain (original logic).
     *
     * 1. Ensure ON_DEMAND mode
     * 2. Loop: CMD_CODE=10 -> parse -> ACK -> repeat until FIFO empty or limit reached
     */
    private suspend fun fetchTransactionsPull(limit: Int): TransactionBatch {
        val transactions = mutableListOf<CanonicalTransaction>()

        // Step 1: Ensure ON_DEMAND mode
        if (!ensureMode(MODE_ON_DEMAND)) {
            AppLogger.w(TAG, "fetchTransactionsPull: failed to set ON_DEMAND mode")
            return TransactionBatch(
                transactions = emptyList(),
                hasMore = false,
            )
        }

        // Step 2: FIFO drain loop
        for (i in 0 until limit) {
            try {
                val token = nextToken()
                val tokenStr = token.toString()

                // Send CMD_CODE=10 — request next transaction
                val requestBody = RadixXmlBuilder.buildTransactionRequest(tokenStr, sharedSecret)
                val headers = RadixXmlBuilder.buildHttpHeaders(usnCode, RadixXmlBuilder.OPERATION_TRANSACTION)
                val url = "http://${config.hostAddress}:$transactionPort"

                val response = httpClient.post(url) {
                    headers.forEach { (key, value) -> header(key, value) }
                    setBody(requestBody)
                }

                val responseBody = response.bodyAsText()
                val parseResult = RadixXmlParser.parseTransactionResponse(responseBody)

                when (parseResult) {
                    is RadixParseResult.Success -> {
                        val txnResp = parseResult.value

                        when (txnResp.respCode) {
                            RESP_CODE_SUCCESS -> {
                                // Transaction available — parse and normalize
                                val trn = txnResp.transaction
                                if (trn != null) {
                                    val rawEnvelope = RawPayloadEnvelope(
                                        vendor = FccVendor.RADIX,
                                        siteCode = config.siteCode,
                                        receivedAtUtc = Instant.now().toString(),
                                        contentType = "text/xml",
                                        payload = responseBody,
                                    )

                                    when (val normResult = normalize(rawEnvelope)) {
                                        is NormalizationResult.Success -> {
                                            transactions.add(normResult.transaction)
                                        }
                                        is NormalizationResult.Failure -> {
                                            AppLogger.w(TAG, "fetchTransactions: normalization failed: ${normResult.errorCode} — ${normResult.message}")
                                        }
                                    }
                                }

                                // ACK with CMD_CODE=201 to dequeue from FCC FIFO
                                val ackBody = RadixXmlBuilder.buildTransactionAck(tokenStr, sharedSecret)
                                httpClient.post(url) {
                                    headers.forEach { (key, value) -> header(key, value) }
                                    setBody(ackBody)
                                }
                            }
                            RESP_CODE_FIFO_EMPTY -> {
                                // FIFO empty — no more transactions
                                AppLogger.d(TAG, "fetchTransactions: FIFO empty after ${transactions.size} transactions")
                                return TransactionBatch(
                                    transactions = transactions,
                                    hasMore = false,
                                )
                            }
                            RESP_CODE_SIGNATURE_ERROR -> {
                                AppLogger.w(TAG, "fetchTransactions: signature error (RESP_CODE=251) — check sharedSecret")
                                return TransactionBatch(
                                    transactions = transactions,
                                    hasMore = false,
                                )
                            }
                            else -> {
                                AppLogger.w(TAG, "fetchTransactions: unexpected RESP_CODE=${txnResp.respCode}: ${txnResp.respMsg}")
                                return TransactionBatch(
                                    transactions = transactions,
                                    hasMore = false,
                                )
                            }
                        }
                    }
                    is RadixParseResult.Error -> {
                        AppLogger.w(TAG, "fetchTransactions: parse error: ${parseResult.message}")
                        return TransactionBatch(
                            transactions = transactions,
                            hasMore = false,
                        )
                    }
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                // Network or other transient error mid-batch — return transactions
                // collected so far rather than losing them all.
                AppLogger.w(TAG, "fetchTransactionsPull: error on iteration ${i + 1}, " +
                    "returning ${transactions.size} transactions collected so far: ${e.message}")
                return TransactionBatch(
                    transactions = transactions,
                    hasMore = false,
                )
            }
        }

        // Reached limit — there may be more transactions
        return TransactionBatch(
            transactions = transactions,
            hasMore = true,
        )
    }

    // -----------------------------------------------------------------------
    // IFccAdapter — normalize (RX-3.3, RX-4.3)
    // -----------------------------------------------------------------------

    /**
     * Normalizes a raw Radix FCC XML payload into a [CanonicalTransaction].
     *
     * Parsing:
     * - Parses XML from [RawPayloadEnvelope.payload]
     * - Extracts TRN data from `<FDC_RESP>` response
     *
     * Normalization rules:
     * - Dedup key: `{FDC_NUM}-{FDC_SAVE_NUM}`
     * - Volume: litres (decimal string) x 1,000,000 via BigDecimal -> Long microlitres
     * - Amount: decimal string x currency decimal factor (default 100) -> Long minor units
     * - Timestamps: FDC local time -> UTC using config timezone
     * - Pump mapping: PUMP_ADDR/FP -> canonical pump number via fccPumpAddressMap
     * - Product code: mapped via config.productCodeMapping
     * - EFD_ID -> fiscalReceiptNumber
     * - TOKEN correlation: matches active pre-auths for odooOrderId/correlationId
     *
     * NO FLOATING POINT for money/volume — uses BigDecimal then toLong().
     */
    override suspend fun normalize(rawPayload: RawPayloadEnvelope): NormalizationResult {
        return try {
            val xml = rawPayload.payload
            val parseResult = RadixXmlParser.parseTransactionResponse(xml)

            when (parseResult) {
                is RadixParseResult.Error -> {
                    NormalizationResult.Failure(
                        errorCode = "INVALID_PAYLOAD",
                        message = "Failed to parse Radix XML: ${parseResult.message}",
                    )
                }
                is RadixParseResult.Success -> {
                    val resp = parseResult.value
                    val trn = resp.transaction
                        ?: return NormalizationResult.Failure(
                            errorCode = "MISSING_REQUIRED_FIELD",
                            message = "No <TRN> element in response (RESP_CODE=${resp.respCode})",
                        )

                    normalizeTransaction(trn, resp, rawPayload)
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            NormalizationResult.Failure(
                errorCode = "INVALID_PAYLOAD",
                message = "Normalization error: ${e::class.simpleName}: ${e.message}",
            )
        }
    }

    /**
     * Core normalization logic for a parsed TRN element.
     *
     * All monetary and volume conversions use BigDecimal to avoid floating-point errors.
     */
    private fun normalizeTransaction(
        trn: RadixTransactionData,
        resp: RadixTransactionResponse,
        rawPayload: RawPayloadEnvelope,
    ): NormalizationResult {
        // --- Dedup key ---
        if (trn.fdcNum.isBlank() || trn.fdcSaveNum.isBlank()) {
            return NormalizationResult.Failure(
                errorCode = "MISSING_REQUIRED_FIELD",
                message = "FDC_NUM and FDC_SAVE_NUM are required for dedup key",
                fieldName = if (trn.fdcNum.isBlank()) "FDC_NUM" else "FDC_SAVE_NUM",
            )
        }
        val fccTransactionId = "${trn.fdcNum}-${trn.fdcSaveNum}"

        // --- Volume: litres -> microlitres via BigDecimal ---
        val volumeMicrolitres = try {
            if (trn.vol.isBlank()) {
                return NormalizationResult.Failure(
                    errorCode = "MISSING_REQUIRED_FIELD",
                    message = "VOL (volume) is required",
                    fieldName = "VOL",
                )
            }
            BigDecimal(trn.vol)
                .multiply(MICROLITRES_PER_LITRE)
                .setScale(0, RoundingMode.HALF_UP)
                .toLong()
        } catch (e: NumberFormatException) {
            return NormalizationResult.Failure(
                errorCode = "MALFORMED_FIELD",
                message = "Invalid volume value: '${trn.vol}'",
                fieldName = "VOL",
            )
        }

        // --- Amount: decimal string -> minor currency units via BigDecimal ---
        val amountMinorUnits = try {
            if (trn.amo.isBlank()) {
                return NormalizationResult.Failure(
                    errorCode = "MISSING_REQUIRED_FIELD",
                    message = "AMO (amount) is required",
                    fieldName = "AMO",
                )
            }
            BigDecimal(trn.amo)
                .multiply(currencyDecimalFactor)
                .setScale(0, RoundingMode.HALF_UP)
                .toLong()
        } catch (e: NumberFormatException) {
            return NormalizationResult.Failure(
                errorCode = "MALFORMED_FIELD",
                message = "Invalid amount value: '${trn.amo}'",
                fieldName = "AMO",
            )
        }

        if (volumeMicrolitres < 0) {
            return NormalizationResult.Failure(
                errorCode = "MALFORMED_FIELD",
                message = "Volume must not be negative: '${trn.vol}'",
                fieldName = "VOL",
            )
        }

        if (amountMinorUnits < 0) {
            return NormalizationResult.Failure(
                errorCode = "MALFORMED_FIELD",
                message = "Amount must not be negative: '${trn.amo}'",
                fieldName = "AMO",
            )
        }

        // --- Unit price: decimal string -> minor units per litre via BigDecimal ---
        val unitPriceMinorPerLitre = try {
            if (trn.price.isBlank()) {
                0L
            } else {
                BigDecimal(trn.price)
                    .multiply(currencyDecimalFactor)
                    .setScale(0, RoundingMode.HALF_UP)
                    .toLong()
            }
        } catch (e: NumberFormatException) {
            return NormalizationResult.Failure(
                errorCode = "MALFORMED_FIELD",
                message = "Invalid price value: '${trn.price}'",
                fieldName = "PRICE",
            )
        }

        // --- Timestamps: FDC local -> UTC ---
        val (startedAt, completedAt) = try {
            convertTimestamps(trn.fdcDate, trn.fdcTime)
        } catch (e: Exception) {
            return NormalizationResult.Failure(
                errorCode = "MALFORMED_FIELD",
                message = "Invalid date/time: FDC_DATE='${trn.fdcDate}', FDC_TIME='${trn.fdcTime}': ${e.message}",
                fieldName = "FDC_DATE",
            )
        }

        // --- Pump number: resolve from PUMP_ADDR/FP via address map ---
        val pumpNumber = resolvePumpNumber(trn.pumpAddr, trn.fp)

        // --- Nozzle number ---
        val nozzleNumber = trn.noz.toIntOrNull() ?: 0

        // --- Product code mapping ---
        val rawProductCode = trn.fdcProd.ifBlank { trn.rdgProd }
        val productCode = config.productCodeMapping[rawProductCode] ?: "UNKNOWN"

        // --- Fiscal receipt number ---
        val fiscalReceiptNumber = trn.efdId.ifBlank { null }

        // --- TOKEN correlation (RX-4.3) ---
        val responseToken = resp.token.trim().toIntOrNull() ?: 0
        val preAuthEntry = if (responseToken != 0) {
            activePreAuths.remove(responseToken)
        } else {
            null // TOKEN=0 means Normal Order (no pre-auth)
        }

        val correlationId = preAuthEntry?.let { "RADIX-TOKEN-${it.token}" }
            ?: UUID.randomUUID().toString()
        val odooOrderId = preAuthEntry?.odooOrderId

        val now = Instant.now().toString()

        return NormalizationResult.Success(
            CanonicalTransaction(
                id = UUID.randomUUID().toString(),
                fccTransactionId = fccTransactionId,
                siteCode = rawPayload.siteCode,
                pumpNumber = pumpNumber,
                nozzleNumber = nozzleNumber,
                productCode = productCode,
                volumeMicrolitres = volumeMicrolitres,
                amountMinorUnits = amountMinorUnits,
                unitPriceMinorPerLitre = unitPriceMinorPerLitre,
                startedAt = startedAt,
                completedAt = completedAt,
                fccVendor = FccVendor.RADIX,
                legalEntityId = "", // Populated by upstream orchestrator
                currencyCode = config.currencyCode,
                status = TransactionStatus.PENDING,
                ingestionSource = IngestionSource.EDGE_UPLOAD,
                ingestedAt = now,
                updatedAt = now,
                schemaVersion = 1,
                isDuplicate = false,
                correlationId = correlationId,
                fiscalReceiptNumber = fiscalReceiptNumber,
                rawPayloadJson = rawPayload.payload,
                odooOrderId = odooOrderId,
            )
        )
    }

    // -----------------------------------------------------------------------
    // IFccAdapter — sendPreAuth (RX-4.1)
    // -----------------------------------------------------------------------

    /**
     * Sends a pre-authorization command to the Radix FCC.
     *
     * 1. Resolves pump from command.pumpNumber using fccPumpAddressMap -> (PUMP_ADDR, FP)
     * 2. Generates TOKEN from counter, tracks in ConcurrentHashMap
     * 3. Builds AUTH_DATA XML via RadixXmlBuilder.buildPreAuthRequest()
     * 4. POSTs to auth port P
     * 5. Parses response ACKCODE:
     *    - 0   -> AUTHORIZED
     *    - 251 -> DECLINED (pump not found / signature error)
     *    - 255 -> DECLINED (nozzle not lifted / bad XML)
     *    - 256 -> IN_PROGRESS (already authorized / bad header)
     *    - 258 -> DECLINED (max exceeded / pump not ready)
     *    - 260 -> ERROR (system error / DSB offline)
     */
    override suspend fun sendPreAuth(command: PreAuthCommand): PreAuthResult {
        return try {
            withTimeout(PREAUTH_TIMEOUT_MS) {
                // Step 1: Resolve pump address
                val pumpEntry = pumpAddressMap[command.pumpNumber]
                if (pumpEntry == null) {
                    return@withTimeout PreAuthResult(
                        status = PreAuthResultStatus.DECLINED,
                        message = "Pump ${command.pumpNumber} not found in fccPumpAddressMap",
                    )
                }

                // Step 2: Generate TOKEN and track pre-auth
                val token = nextToken()
                val preAuthEntry = PreAuthEntry(
                    token = token,
                    pumpNumber = command.pumpNumber,
                    odooOrderId = command.odooOrderId,
                    createdAt = Instant.now(),
                )
                activePreAuths[token] = preAuthEntry

                // Step 3: Build AUTH_DATA XML
                val presetAmount = BigDecimal(command.amountMinorUnits)
                    .divide(currencyDecimalFactor, 2, RoundingMode.HALF_UP)
                    .toPlainString()

                val params = RadixPreAuthParams(
                    pump = pumpEntry.pumpAddr,
                    fp = pumpEntry.fp,
                    authorize = true,
                    product = 0, // 0 = all products
                    presetVolume = "0.00", // Volume preset not used — amount-based
                    presetAmount = presetAmount,
                    customerName = command.customerName,
                    customerIdType = command.customerIdType,
                    customerId = command.customerTaxId,
                    mobileNumber = command.customerPhone,
                    token = token.toString(),
                )

                val requestBody = RadixXmlBuilder.buildPreAuthRequest(params, sharedSecret)
                val headers = RadixXmlBuilder.buildHttpHeaders(usnCode, RadixXmlBuilder.OPERATION_AUTHORIZE)
                val url = "http://${config.hostAddress}:$authPort"

                // Step 4: POST to auth port
                val response = httpClient.post(url) {
                    headers.forEach { (key, value) -> header(key, value) }
                    setBody(requestBody)
                }

                val responseBody = response.bodyAsText()
                val parseResult = RadixXmlParser.parseAuthResponse(responseBody)

                // Step 5: Map ACKCODE to PreAuthResult
                when (parseResult) {
                    is RadixParseResult.Success -> {
                        mapAckCodeToResult(parseResult.value, token, preAuthEntry)
                    }
                    is RadixParseResult.Error -> {
                        activePreAuths.remove(token)
                        PreAuthResult(
                            status = PreAuthResultStatus.ERROR,
                            message = "Failed to parse auth response: ${parseResult.message}",
                        )
                    }
                }
            }
        } catch (e: TimeoutCancellationException) {
            PreAuthResult(
                status = PreAuthResultStatus.TIMEOUT,
                message = "Pre-auth request timed out after ${PREAUTH_TIMEOUT_MS}ms",
            )
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            PreAuthResult(
                status = PreAuthResultStatus.ERROR,
                message = "Pre-auth failed: ${e::class.simpleName}: ${e.message}",
            )
        }
    }

    /**
     * Maps Radix ACKCODE to canonical [PreAuthResult].
     */
    private fun mapAckCodeToResult(
        authResp: RadixAuthResponse,
        token: Int,
        preAuthEntry: PreAuthEntry,
    ): PreAuthResult {
        return when (authResp.ackCode) {
            ACKCODE_SUCCESS -> {
                // Keep entry in activePreAuths for later TOKEN correlation
                PreAuthResult(
                    status = PreAuthResultStatus.AUTHORIZED,
                    authorizationCode = "RADIX-TOKEN-$token",
                    correlationId = "RADIX-TOKEN-$token",
                    message = authResp.ackMsg.ifBlank { "Authorized" },
                )
            }
            ACKCODE_SIGNATURE_ERROR -> {
                activePreAuths.remove(token)
                PreAuthResult(
                    status = PreAuthResultStatus.DECLINED,
                    message = "Signature error (ACKCODE=251) — check sharedSecret configuration",
                )
            }
            ACKCODE_BAD_XML -> {
                activePreAuths.remove(token)
                PreAuthResult(
                    status = PreAuthResultStatus.DECLINED,
                    message = "Nozzle not lifted or bad XML format (ACKCODE=255): ${authResp.ackMsg}",
                )
            }
            ACKCODE_BAD_HEADER -> {
                // 256 can mean already authorized — treat as IN_PROGRESS
                PreAuthResult(
                    status = PreAuthResultStatus.IN_PROGRESS,
                    message = "Already authorized or bad header (ACKCODE=256): ${authResp.ackMsg}",
                    correlationId = "RADIX-TOKEN-$token",
                )
            }
            ACKCODE_PUMP_NOT_READY -> {
                activePreAuths.remove(token)
                PreAuthResult(
                    status = PreAuthResultStatus.DECLINED,
                    message = "Pump not ready or max exceeded (ACKCODE=258): ${authResp.ackMsg}",
                )
            }
            ACKCODE_DSB_OFFLINE -> {
                activePreAuths.remove(token)
                PreAuthResult(
                    status = PreAuthResultStatus.ERROR,
                    message = "DSB offline or system error (ACKCODE=260): ${authResp.ackMsg}",
                )
            }
            else -> {
                activePreAuths.remove(token)
                PreAuthResult(
                    status = PreAuthResultStatus.ERROR,
                    message = "Unknown ACKCODE=${authResp.ackCode}: ${authResp.ackMsg}",
                )
            }
        }
    }

    // -----------------------------------------------------------------------
    // IFccAdapter — cancelPreAuth
    // -----------------------------------------------------------------------

    /**
     * Cancels an active pre-authorization on the Radix FCC.
     *
     * Sends AUTH_DATA with AUTH=FALSE to the auth port. Uses the pump address mapping
     * to resolve the pump. If a TOKEN is available from fccCorrelationId (format
     * "RADIX-TOKEN-xxx"), it is included; otherwise a fresh token is generated.
     *
     * Idempotent: if the pre-auth is already cancelled or dispensed, the FCC
     * returns a non-zero ACKCODE but the pump is not in an authorized state.
     */
    override suspend fun cancelPreAuth(command: CancelPreAuthCommand): Boolean {
        return try {
            withTimeout(PREAUTH_TIMEOUT_MS) {
                val pumpEntry = pumpAddressMap[command.pumpNumber]
                if (pumpEntry == null) {
                    AppLogger.w(TAG, "Cancel pre-auth: pump ${command.pumpNumber} not in fccPumpAddressMap")
                    return@withTimeout false
                }

                // Extract token from correlationId if available (format: "RADIX-TOKEN-123")
                val token = command.fccCorrelationId
                    ?.removePrefix("RADIX-TOKEN-")
                    ?.toIntOrNull()
                    ?.also { activePreAuths.remove(it) }

                val tokenStr = (token ?: nextToken()).toString()

                val requestBody = RadixXmlBuilder.buildPreAuthCancelRequest(
                    pump = pumpEntry.pumpAddr,
                    fp = pumpEntry.fp,
                    token = tokenStr,
                    secret = sharedSecret,
                )
                val headers = RadixXmlBuilder.buildHttpHeaders(usnCode, RadixXmlBuilder.OPERATION_AUTHORIZE)
                val url = "http://${config.hostAddress}:$authPort"

                val response = httpClient.post(url) {
                    headers.forEach { (key, value) -> header(key, value) }
                    setBody(requestBody)
                }

                val responseBody = response.bodyAsText()
                val parseResult = RadixXmlParser.parseAuthResponse(responseBody)

                when (parseResult) {
                    is RadixParseResult.Success -> {
                        // ACKCODE 0 = success; any other code means the pump was not
                        // in an authorized state — treat as idempotent success.
                        AppLogger.i(TAG, "Cancel pre-auth ACKCODE=${parseResult.value.ackCode} " +
                            "for pump=${command.pumpNumber}")
                        true
                    }
                    is RadixParseResult.Error -> {
                        AppLogger.w(TAG, "Cancel pre-auth parse error for pump=${command.pumpNumber}: " +
                            parseResult.message)
                        false
                    }
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            AppLogger.w(TAG, "Cancel pre-auth failed for pump=${command.pumpNumber}: " +
                "${e::class.simpleName}: ${e.message}")
            false
        }
    }

    // -----------------------------------------------------------------------
    // IFccAdapter — getPumpStatus
    // -----------------------------------------------------------------------

    /**
     * Synthesizes pump status from configured pumps and active pre-auth state.
     *
     * Radix protocol does not expose real-time pump status, but we can derive
     * meaningful state: pumps with active pre-auths are AUTHORIZED, others are IDLE.
     * Uses [PumpStatusSynthesizer] with pump numbers extracted from [pumpAddressMap].
     */
    override suspend fun getPumpStatus(): List<PumpStatus> {
        val configuredPumps = pumpAddressMap.keys
        if (configuredPumps.isEmpty()) return emptyList()

        return PumpStatusSynthesizer.synthesize(
            configuredPumps = configuredPumps,
            activePreAuths = preAuthMatcher.getActivePreAuths(),
            siteCode = config.siteCode,
            currencyCode = config.currencyCode,
        )
    }

    // -----------------------------------------------------------------------
    // IFccAdapter — acknowledgeTransactions
    // -----------------------------------------------------------------------

    /** No-op — Radix ACK (CMD_CODE=201) is sent inline during the fetch loop. */
    override suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean = true

    // -----------------------------------------------------------------------
    // Private — timestamp conversion
    // -----------------------------------------------------------------------

    /**
     * Converts FDC local date + time strings to UTC ISO 8601 timestamps.
     *
     * @param fdcDate FDC local date (e.g. "2021-03-03")
     * @param fdcTime FDC local time (e.g. "21:17:53")
     * @return Pair of (startedAt, completedAt) in UTC ISO 8601 format.
     *         For Radix, startedAt == completedAt (single timestamp per transaction).
     */
    private fun convertTimestamps(fdcDate: String, fdcTime: String): Pair<String, String> {
        if (fdcDate.isBlank() || fdcTime.isBlank()) {
            // Fall back to current UTC time if no timestamp available
            val now = Instant.now().toString()
            return Pair(now, now)
        }

        val localDateTime = LocalDateTime.parse(
            "${fdcDate}T${fdcTime}",
            DateTimeFormatter.ISO_LOCAL_DATE_TIME,
        )
        val utcInstant = localDateTime.atZone(siteTimezone).toInstant()
        val utcString = utcInstant.toString()
        return Pair(utcString, utcString)
    }

    // -----------------------------------------------------------------------
    // Private — pump address resolution
    // -----------------------------------------------------------------------

    /**
     * Resolves a canonical pump number from FCC-native PUMP_ADDR and FP values.
     *
     * Uses the reverse pump address map. Falls back to PUMP_ADDR + pumpNumberOffset
     * if no mapping is found.
     */
    private fun resolvePumpNumber(pumpAddr: String, fp: String): Int {
        val key = "${pumpAddr.ifBlank { "0" }}-${fp.ifBlank { "0" }}"
        return reversePumpAddressMap[key]
            ?: ((pumpAddr.toIntOrNull() ?: 0) + config.pumpNumberOffset)
    }

    // -----------------------------------------------------------------------
    // Private — pump address map parsing
    // -----------------------------------------------------------------------

    /**
     * Parses the fccPumpAddressMap JSON string into a typed map.
     *
     * Expected JSON format:
     * ```json
     * {
     *   "1": { "pumpAddr": 0, "fp": 0 },
     *   "2": { "pumpAddr": 0, "fp": 1 },
     *   "3": { "pumpAddr": 1, "fp": 0 }
     * }
     * ```
     *
     * Returns empty map if the string is null, blank, or malformed.
     */
    private fun parsePumpAddressMap(json: String?): Map<Int, PumpAddressEntry> {
        if (json.isNullOrBlank()) return emptyMap()

        return try {
            val result = mutableMapOf<Int, PumpAddressEntry>()
            val skipped = mutableListOf<String>()
            val root = org.json.JSONObject(json)
            for (key in root.keys()) {
                val pumpNumber = key.toIntOrNull()
                if (pumpNumber == null) {
                    skipped.add("key '$key' is not a valid pump number")
                    continue
                }
                val obj = root.getJSONObject(key)
                val pumpAddr = obj.optInt("pumpAddr", -1)
                val fp = obj.optInt("fp", -1)
                if (pumpAddr < 0 || fp < 0) {
                    skipped.add("pump $pumpNumber has invalid pumpAddr=$pumpAddr or fp=$fp")
                    continue
                }
                result[pumpNumber] = PumpAddressEntry(pumpAddr, fp)
            }
            if (skipped.isNotEmpty()) {
                AppLogger.w(TAG, "Skipped ${skipped.size} invalid pump entries: ${skipped.joinToString("; ")}")
            }
            if (result.isEmpty() && root.length() > 0) {
                AppLogger.e(TAG, "fccPumpAddressMap had ${root.length()} entries but ALL were invalid — pre-auth will not work")
            }
            result
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to parse fccPumpAddressMap — pre-auth will not work: ${e.message}", e)
            emptyMap()
        }
    }

    // -----------------------------------------------------------------------
    // Companion — constants
    // -----------------------------------------------------------------------

    companion object {
        private const val TAG = "RadixAdapter"
        val VENDOR = FccVendor.RADIX
        const val ADAPTER_VERSION = "1.0.0"
        const val PROTOCOL = "HTTP_XML"

        /** Returns true if this adapter has a working implementation. */
        const val IS_IMPLEMENTED = true

        /** M-22: Use centralised timeout constants. */
        val HEARTBEAT_TIMEOUT_MS = AdapterTimeouts.HEARTBEAT_TIMEOUT_MS
        val PREAUTH_TIMEOUT_MS = AdapterTimeouts.PREAUTH_TIMEOUT_MS

        /** Maximum number of transactions to fetch in a single batch. */
        const val MAX_FETCH_LIMIT = 200

        /** Token counter wrap boundary: 0-65535. */
        const val TOKEN_WRAP = 65536

        // --- Radix response codes ---

        /** Successful response code (RESP_CODE / ACKCODE). */
        const val RESP_CODE_SUCCESS = 201

        /** FIFO buffer empty — no more transactions. */
        const val RESP_CODE_FIFO_EMPTY = 205

        /** Signature error response code — indicates misconfigured shared secret. */
        const val RESP_CODE_SIGNATURE_ERROR = 251

        // --- Radix ACKCODE values (auth responses) ---

        /** Pre-auth success. */
        const val ACKCODE_SUCCESS = 0

        /** Signature error — check shared secret. */
        const val ACKCODE_SIGNATURE_ERROR = 251

        /** Bad XML format / nozzle not lifted. */
        const val ACKCODE_BAD_XML = 255

        /** Bad header format / already authorized. */
        const val ACKCODE_BAD_HEADER = 256

        /** Pump not ready / max exceeded. */
        const val ACKCODE_PUMP_NOT_READY = 258

        /** DSB offline / system error. */
        const val ACKCODE_DSB_OFFLINE = 260

        // --- Mode constants ---

        /** Mode not yet known or reset. */
        const val MODE_UNKNOWN = -1

        /** ON_DEMAND (pull) mode — host requests transactions. Radix protocol MODE=1. */
        const val MODE_ON_DEMAND = 1

        /** UNSOLICITED (push) mode — FDC posts transactions to the listener. */
        const val MODE_UNSOLICITED = 2

        /** RESP_CODE for unsolicited push transactions from the FDC. */
        const val RESP_CODE_UNSOLICITED = 30

        /** Volume conversion factor: 1 litre = 1,000,000 microlitres. */
        private val MICROLITRES_PER_LITRE = BigDecimal(1_000_000)

        /** TTL for pre-auth entries: entries older than this are purged to prevent memory leaks. */
        private const val PREAUTH_TTL_MINUTES = 30L
    }
}

// ---------------------------------------------------------------------------
// Supporting data classes
// ---------------------------------------------------------------------------

/**
 * Tracks an active pre-authorization for later TOKEN correlation with
 * the resulting dispense transaction.
 *
 * Stored in [RadixAdapter.activePreAuths] keyed by TOKEN value.
 */
data class PreAuthEntry(
    /** Radix TOKEN value (0-65535) assigned to this pre-auth. */
    val token: Int,
    /** Canonical pump number from the original PreAuthCommand. */
    val pumpNumber: Int,
    /** Odoo order ID for correlation, if provided. */
    val odooOrderId: String?,
    /** When this pre-auth was created. */
    val createdAt: Instant,
)

/**
 * Parsed entry from the fccPumpAddressMap configuration.
 *
 * Maps a canonical pump number to the FCC-native (PUMP_ADDR, FP) pair.
 */
data class PumpAddressEntry(
    /** DSB/RDG unit address in the Radix FCC. */
    val pumpAddr: Int,
    /** Filling point within the DSB/RDG unit. */
    val fp: Int,
)

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
import java.math.BigDecimal
import java.math.RoundingMode
import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.UUID
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
 */
class RadixAdapter(
    private val config: AgentFccConfig,
    private val httpClient: HttpClient = HttpClient(OkHttp),
) : IFccAdapter {

    // -----------------------------------------------------------------------
    // Token counter — shared across heartbeat, fetch, and pre-auth
    // -----------------------------------------------------------------------

    /** Sequential token counter (0-65535, wraps at 65536). Thread-safe. */
    private val tokenCounter = AtomicInteger(0)

    /** Generates the next sequential token (0-65535), wrapping at 65536. Thread-safe. */
    private fun nextToken(): Int {
        return tokenCounter.getAndUpdate { (it + 1) % TOKEN_WRAP }
    }

    // -----------------------------------------------------------------------
    // Mode management (RX-3.5)
    // -----------------------------------------------------------------------

    /**
     * Cached current FCC transaction transfer mode.
     *   -1 = unknown (not yet set or reset after connectivity loss)
     *    0 = ON_DEMAND (pull mode)
     *    1 = OFF
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
                        Log.d(TAG, "Mode changed to $mode")
                        true
                    } else {
                        Log.w(TAG, "Mode change failed: RESP_CODE=${parseResult.value.respCode}")
                        false
                    }
                }
                is RadixParseResult.Error -> {
                    Log.w(TAG, "Mode change parse error: ${parseResult.message}")
                    false
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.w(TAG, "Mode change failed: ${e::class.simpleName}: ${e.message}")
            resetModeState()
            false
        }
    }

    /** Reset cached mode on connectivity loss. */
    private fun resetModeState() {
        currentMode = MODE_UNKNOWN
    }

    // -----------------------------------------------------------------------
    // Pre-auth tracking (RX-4.1, RX-4.3)
    // -----------------------------------------------------------------------

    /** Active pre-auth entries keyed by TOKEN for later transaction correlation. */
    private val activePreAuths = ConcurrentHashMap<Int, PreAuthEntry>()

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
            Log.w(TAG, "Invalid timezone '${config.timezone}', falling back to UTC")
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
            resetModeState()
            false
        }
    }

    // -----------------------------------------------------------------------
    // IFccAdapter — fetchTransactions (RX-3.1)
    // -----------------------------------------------------------------------

    /**
     * Fetches transactions from the Radix FCC using FIFO drain.
     *
     * 1. Ensure ON_DEMAND mode (CMD_CODE=20, MODE=0)
     * 2. Loop: send CMD_CODE=10 (request next transaction) -> parse response
     * 3. RESP_CODE=201: transaction available -> parse TRN data -> ACK with CMD_CODE=201 -> continue
     * 4. RESP_CODE=205: FIFO empty -> break
     * 5. Stop at cursor.limit transactions
     *
     * ACK is sent inline during the fetch loop — acknowledgeTransactions() is a no-op.
     */
    override suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch {
        val limit = cursor.limit.coerceIn(1, MAX_FETCH_LIMIT)
        val transactions = mutableListOf<CanonicalTransaction>()

        try {
            // Step 1: Ensure ON_DEMAND mode
            if (!ensureMode(MODE_ON_DEMAND)) {
                Log.w(TAG, "fetchTransactions: failed to set ON_DEMAND mode")
                return TransactionBatch(
                    transactions = emptyList(),
                    hasMore = false,
                )
            }

            // Step 2: FIFO drain loop
            for (i in 0 until limit) {
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
                                        siteCode = config.hostAddress,
                                        receivedAtUtc = Instant.now().toString(),
                                        contentType = "text/xml",
                                        payload = responseBody,
                                    )

                                    when (val normResult = normalize(rawEnvelope)) {
                                        is NormalizationResult.Success -> {
                                            transactions.add(normResult.transaction)
                                        }
                                        is NormalizationResult.Failure -> {
                                            Log.w(TAG, "fetchTransactions: normalization failed: ${normResult.errorCode} — ${normResult.message}")
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
                                Log.d(TAG, "fetchTransactions: FIFO empty after ${transactions.size} transactions")
                                return TransactionBatch(
                                    transactions = transactions,
                                    hasMore = false,
                                )
                            }
                            RESP_CODE_SIGNATURE_ERROR -> {
                                Log.w(TAG, "fetchTransactions: signature error (RESP_CODE=251) — check sharedSecret")
                                return TransactionBatch(
                                    transactions = transactions,
                                    hasMore = false,
                                )
                            }
                            else -> {
                                Log.w(TAG, "fetchTransactions: unexpected RESP_CODE=${txnResp.respCode}: ${txnResp.respMsg}")
                                return TransactionBatch(
                                    transactions = transactions,
                                    hasMore = false,
                                )
                            }
                        }
                    }
                    is RadixParseResult.Error -> {
                        Log.w(TAG, "fetchTransactions: parse error: ${parseResult.message}")
                        return TransactionBatch(
                            transactions = transactions,
                            hasMore = false,
                        )
                    }
                }
            }

            // Reached limit — there may be more transactions
            return TransactionBatch(
                transactions = transactions,
                hasMore = true,
            )
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.w(TAG, "fetchTransactions: ${e::class.simpleName}: ${e.message}")
            resetModeState()
            return TransactionBatch(
                transactions = transactions,
                hasMore = false,
            )
        }
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
    // IFccAdapter — getPumpStatus
    // -----------------------------------------------------------------------

    /** Radix does not expose real-time pump status. Always returns empty list. */
    override suspend fun getPumpStatus(): List<PumpStatus> = emptyList()

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
            // Simple JSON parsing without external library dependency.
            // Format: { "key": { "pumpAddr": N, "fp": N }, ... }
            val trimmed = json.trim()
            if (!trimmed.startsWith("{") || !trimmed.endsWith("}")) return emptyMap()

            // Remove outer braces
            val inner = trimmed.substring(1, trimmed.length - 1).trim()
            if (inner.isEmpty()) return emptyMap()

            // Split by top-level entries: "key": { ... }
            // Use a simple state machine to handle nested braces
            var depth = 0
            var start = 0
            val entries = mutableListOf<String>()
            for (i in inner.indices) {
                when (inner[i]) {
                    '{' -> depth++
                    '}' -> {
                        depth--
                        if (depth == 0) {
                            entries.add(inner.substring(start, i + 1).trim())
                            start = i + 1
                            // Skip comma
                            while (start < inner.length && (inner[start] == ',' || inner[start].isWhitespace())) {
                                start++
                            }
                        }
                    }
                }
            }

            for (entry in entries) {
                // "1": { "pumpAddr": 0, "fp": 0 }
                val colonIdx = entry.indexOf(':')
                if (colonIdx < 0) continue

                val keyStr = entry.substring(0, colonIdx).trim().removeSurrounding("\"")
                val pumpNumber = keyStr.toIntOrNull() ?: continue

                val objStr = entry.substring(colonIdx + 1).trim()
                val pumpAddr = extractJsonInt(objStr, "pumpAddr")
                val fp = extractJsonInt(objStr, "fp")

                if (pumpAddr != null && fp != null) {
                    result[pumpNumber] = PumpAddressEntry(pumpAddr, fp)
                }
            }

            result
        } catch (e: Exception) {
            Log.w(TAG, "Failed to parse fccPumpAddressMap: ${e.message}")
            emptyMap()
        }
    }

    /**
     * Extracts an integer value for a given key from a simple JSON object string.
     * E.g., from `{ "pumpAddr": 0, "fp": 1 }` extracts 0 for key "pumpAddr".
     */
    private fun extractJsonInt(json: String, key: String): Int? {
        // Look for "key" : N or "key": N
        val patterns = listOf("\"$key\":", "\"$key\" :")
        for (pattern in patterns) {
            val idx = json.indexOf(pattern)
            if (idx >= 0) {
                val valueStart = idx + pattern.length
                val valueStr = json.substring(valueStart).trim()
                val sb = StringBuilder()
                for (c in valueStr) {
                    if (c.isDigit() || c == '-') sb.append(c)
                    else break
                }
                return sb.toString().toIntOrNull()
            }
        }
        return null
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

        /** Hard timeout for heartbeat probe (5 seconds). */
        const val HEARTBEAT_TIMEOUT_MS = 5000L

        /** Hard timeout for pre-auth requests (10 seconds). */
        const val PREAUTH_TIMEOUT_MS = 10_000L

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

        /** ON_DEMAND (pull) mode — host requests transactions. */
        const val MODE_ON_DEMAND = 0

        /** Volume conversion factor: 1 litre = 1,000,000 microlitres. */
        private val MICROLITRES_PER_LITRE = BigDecimal(1_000_000)
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

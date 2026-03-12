package com.fccmiddleware.edge.adapter.petronite

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.*
import io.ktor.client.HttpClient
import io.ktor.client.engine.okhttp.OkHttp
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.request.post
import io.ktor.client.request.setBody
import io.ktor.client.statement.bodyAsText
import io.ktor.http.ContentType
import io.ktor.http.HttpStatusCode
import io.ktor.http.contentType
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import java.math.BigDecimal
import java.math.RoundingMode
import java.time.Instant
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/**
 * PetroniteAdapter -- Edge Agent adapter for the Petronite FCC protocol.
 *
 * Communicates with the FCC over station LAN using REST/JSON with OAuth2 Client Credentials:
 *   Auth     : OAuth2 Client Credentials (POST /oauth/token with Basic auth)
 *   Heartbeat: GET /nozzles/assigned as liveness probe
 *   Fetch    : Push-only via webhook -- fetchTransactions returns empty (no pull)
 *   Pre-auth : Two-step: POST /direct-authorize-requests/create + /authorize
 *   Cancel   : POST /direct-authorize-requests/{id}/cancel
 *   Pump     : Synthesized from nozzle assignments + pending orders
 *
 * All monetary and volume conversions use BigDecimal to avoid floating-point errors.
 */
class PetroniteAdapter(
    private val config: AgentFccConfig,
    private val httpClient: HttpClient = HttpClient(OkHttp),
) : IFccAdapter {

    private val json = Json { ignoreUnknownKeys = true }

    private val oauthClient = PetroniteOAuthClient(config, httpClient, json)
    private val nozzleResolver = PetroniteNozzleResolver(config, oauthClient, httpClient, json)

    /**
     * Active pre-authorizations keyed by Petronite OrderId.
     * Thread-safe for concurrent normalize / sendPreAuth / cancelPreAuth calls.
     */
    private val activePreAuths = ConcurrentHashMap<String, ActivePreAuth>()

    /** Derived base URL from config. */
    private val baseUrl: String
        get() = config.hostAddress.trimEnd('/')

    // -----------------------------------------------------------------------
    // IFccAdapter -- fetchTransactions (push-only, no-op)
    // -----------------------------------------------------------------------

    /**
     * Push-only -- Petronite transactions arrive via webhook, not polling.
     * Always returns an empty batch.
     */
    override suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch {
        return TransactionBatch(
            transactions = emptyList(),
            hasMore = false,
        )
    }

    // -----------------------------------------------------------------------
    // IFccAdapter -- normalize (PN-2.1)
    // -----------------------------------------------------------------------

    /**
     * Parses a Petronite webhook JSON payload into a [CanonicalTransaction].
     *
     * - Volume: litres (decimal string) x 1,000,000 via BigDecimal -> Long microlitres
     * - Amount: major units (decimal string) x currencyFactor -> Long minor units
     * - Nozzle ID: reverse mapping via [PetroniteNozzleResolver]
     * - PUMA_ORDER: pre-auth correlation via [activePreAuths]
     *
     * NO FLOATING POINT for money/volume -- uses BigDecimal then toLong().
     */
    override suspend fun normalize(rawPayload: RawPayloadEnvelope): NormalizationResult {
        return try {
            val webhook = try {
                json.decodeFromString<PetroniteWebhookPayload>(rawPayload.payload)
            } catch (e: Exception) {
                return NormalizationResult.Failure(
                    errorCode = "INVALID_PAYLOAD",
                    message = "Failed to parse Petronite webhook JSON: ${e.message}",
                )
            }

            if (!webhook.eventType.equals("transaction.completed", ignoreCase = true)) {
                return NormalizationResult.Failure(
                    errorCode = "UNSUPPORTED_MESSAGE_TYPE",
                    message = "Unsupported event type '${webhook.eventType}' (expected 'transaction.completed')",
                )
            }

            val tx = webhook.transaction
                ?: return NormalizationResult.Failure(
                    errorCode = "MISSING_REQUIRED_FIELD",
                    message = "Webhook payload has no transaction data",
                    fieldName = "transaction",
                )

            normalizeTransaction(tx, rawPayload)
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
     * Core normalization logic for a parsed Petronite transaction.
     * All monetary and volume conversions use BigDecimal.
     */
    private fun normalizeTransaction(
        tx: PetroniteTransactionData,
        rawPayload: RawPayloadEnvelope,
    ): NormalizationResult {
        // --- Nozzle ID reverse mapping ---
        var pumpNumber = tx.pumpNumber
        var nozzleNumber = tx.nozzleNumber
        try {
            val snapshot = nozzleResolver.getCurrentSnapshot()
            val canonical = snapshot[tx.nozzleId]
            if (canonical != null) {
                pumpNumber = canonical.pumpNumber
                nozzleNumber = canonical.nozzleNumber
            }
        } catch (e: Exception) {
            AppLogger.d(TAG, "Nozzle reverse-map failed for '${tx.nozzleId}', using webhook values: ${e.message}")
        }

        // --- Volume: litres -> microlitres via BigDecimal ---
        val volumeMicrolitres = try {
            if (tx.volumeLitres.isBlank()) {
                return NormalizationResult.Failure(
                    errorCode = "MISSING_REQUIRED_FIELD",
                    message = "volumeLitres is required",
                    fieldName = "volumeLitres",
                )
            }
            BigDecimal(tx.volumeLitres)
                .multiply(MICROLITRES_PER_LITRE)
                .setScale(0, RoundingMode.HALF_UP)
                .toLong()
        } catch (e: NumberFormatException) {
            return NormalizationResult.Failure(
                errorCode = "MALFORMED_FIELD",
                message = "Invalid volumeLitres value: '${tx.volumeLitres}'",
                fieldName = "volumeLitres",
            )
        }

        // --- Amount: major units -> minor units via BigDecimal ---
        val currencyFactor = getCurrencyFactor(tx.currency)
        val amountMinorUnits = try {
            if (tx.amountMajor.isBlank()) {
                return NormalizationResult.Failure(
                    errorCode = "MISSING_REQUIRED_FIELD",
                    message = "amountMajor is required",
                    fieldName = "amountMajor",
                )
            }
            BigDecimal(tx.amountMajor)
                .multiply(currencyFactor)
                .setScale(0, RoundingMode.HALF_UP)
                .toLong()
        } catch (e: NumberFormatException) {
            return NormalizationResult.Failure(
                errorCode = "MALFORMED_FIELD",
                message = "Invalid amountMajor value: '${tx.amountMajor}'",
                fieldName = "amountMajor",
            )
        }

        // --- Unit price: major per litre -> minor per litre ---
        val unitPriceMinorPerLitre = try {
            if (tx.unitPrice.isBlank()) {
                0L
            } else {
                BigDecimal(tx.unitPrice)
                    .multiply(currencyFactor)
                    .setScale(0, RoundingMode.HALF_UP)
                    .toLong()
            }
        } catch (e: NumberFormatException) {
            return NormalizationResult.Failure(
                errorCode = "MALFORMED_FIELD",
                message = "Invalid unitPrice value: '${tx.unitPrice}'",
                fieldName = "unitPrice",
            )
        }

        // --- Timestamps ---
        val startedAt = parseTimestamp(tx.startTime) ?: rawPayload.receivedAtUtc
        val completedAt = parseTimestamp(tx.endTime) ?: rawPayload.receivedAtUtc

        // --- Dedup key: {siteCode}-{orderId} ---
        val fccTransactionId = "${rawPayload.siteCode}-${tx.orderId}"

        // --- PUMA_ORDER pre-auth correlation ---
        var correlationId: String? = null
        var odooOrderId: String? = null
        var preAuthId: String? = null

        if (tx.paymentMethod.equals("PUMA_ORDER", ignoreCase = true)) {
            val preAuth = activePreAuths.remove(tx.orderId)
            if (preAuth != null) {
                correlationId = preAuth.orderId
                odooOrderId = preAuth.odooOrderId
                preAuthId = preAuth.preAuthId
                AppLogger.i(TAG, "Correlated transaction ${tx.orderId} with pre-auth (OdooOrderId=$odooOrderId)")
            } else {
                AppLogger.w(TAG, "PUMA_ORDER transaction ${tx.orderId} has no matching active pre-auth")
                // Still set correlationId to OrderId for traceability
                correlationId = tx.orderId
            }
        }

        val now = Instant.now().toString()

        return NormalizationResult.Success(
            CanonicalTransaction(
                id = UUID.randomUUID().toString(),
                fccTransactionId = fccTransactionId,
                siteCode = rawPayload.siteCode,
                pumpNumber = pumpNumber,
                nozzleNumber = nozzleNumber,
                productCode = tx.productCode,
                volumeMicrolitres = volumeMicrolitres,
                amountMinorUnits = amountMinorUnits,
                unitPriceMinorPerLitre = unitPriceMinorPerLitre,
                startedAt = startedAt,
                completedAt = completedAt,
                fccVendor = FccVendor.PETRONITE,
                legalEntityId = rawPayload.siteCode,
                currencyCode = tx.currency,
                status = TransactionStatus.PENDING,
                ingestionSource = IngestionSource.FCC_PUSH,
                ingestedAt = now,
                updatedAt = now,
                schemaVersion = 1,
                isDuplicate = false,
                correlationId = correlationId ?: UUID.randomUUID().toString(),
                fiscalReceiptNumber = tx.receiptCode,
                attendantId = tx.attendantId,
                rawPayloadJson = rawPayload.payload,
                odooOrderId = odooOrderId,
                preAuthId = preAuthId,
            ),
        )
    }

    // -----------------------------------------------------------------------
    // IFccAdapter -- sendPreAuth (PN-3.1 + PN-3.2)
    // -----------------------------------------------------------------------

    /**
     * Two-step pre-auth:
     * 1. POST /direct-authorize-requests/create to create the order
     * 2. POST /direct-authorize-requests/authorize to authorize the pump
     *
     * On 401, invalidates the OAuth token and retries once.
     * Tracks OrderId in the active pre-auth map for later transaction correlation.
     */
    override suspend fun sendPreAuth(command: PreAuthCommand): PreAuthResult {
        return try {
            withTimeout(PREAUTH_TIMEOUT_MS) {
                // Resolve nozzle ID from canonical pump/nozzle numbers.
                val nozzleId = try {
                    nozzleResolver.resolveNozzleId(
                        command.pumpNumber,
                        command.nozzleNumber ?: 1,
                    )
                } catch (e: Exception) {
                    return@withTimeout PreAuthResult(
                        status = PreAuthResultStatus.DECLINED,
                        message = "Cannot resolve nozzle for pump ${command.pumpNumber}: ${e.message}",
                    )
                }

                // Convert from minor units to major units for the Petronite API.
                val currencyFactor = getCurrencyFactor(command.currencyCode)
                val maxAmountMajor = BigDecimal(command.amountMinorUnits)
                    .divide(currencyFactor, getCurrencyDecimals(command.currencyCode), RoundingMode.HALF_UP)
                    .toPlainString()

                // Step 1: Create order.
                val createRequest = PetroniteCreateOrderRequest(
                    nozzleId = nozzleId,
                    maxVolumeLitres = "9999", // No volume limit; amount is the cap.
                    maxAmountMajor = maxAmountMajor,
                    currency = command.currencyCode,
                    externalReference = command.odooOrderId,
                )

                val createResponse = postWithAuthRetry<PetroniteCreateOrderResponse>(
                    path = "/direct-authorize-requests/create",
                    body = json.encodeToString(PetroniteCreateOrderRequest.serializer(), createRequest),
                )

                if (createResponse == null) {
                    return@withTimeout PreAuthResult(
                        status = PreAuthResultStatus.ERROR,
                        message = "Empty Petronite create-order response",
                    )
                }

                AppLogger.i(TAG, "Order created: OrderId=${createResponse.orderId}, Status=${createResponse.status} " +
                    "(pump ${command.pumpNumber})")

                // Step 2: Authorize pump.
                val authRequest = PetroniteAuthorizeRequest(orderId = createResponse.orderId)
                val authBody = json.encodeToString(PetroniteAuthorizeRequest.serializer(), authRequest)

                val authResult = executeAuthorizeStep(
                    authBody = authBody,
                    orderId = createResponse.orderId,
                    nozzleId = nozzleId,
                    command = command,
                )

                authResult
            }
        } catch (e: TimeoutCancellationException) {
            PreAuthResult(
                status = PreAuthResultStatus.TIMEOUT,
                message = "Pre-auth request timed out after ${PREAUTH_TIMEOUT_MS}ms",
            )
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            AppLogger.w(TAG, "Pre-auth failed (pump ${command.pumpNumber}): ${e::class.simpleName}: ${e.message}")
            PreAuthResult(
                status = PreAuthResultStatus.ERROR,
                message = "Pre-auth failed: ${e::class.simpleName}: ${e.message}",
            )
        }
    }

    /**
     * Executes the authorize step of the two-step pre-auth.
     * Handles 401 retry, 400 (nozzle not lifted) -> DECLINED.
     */
    private suspend fun executeAuthorizeStep(
        authBody: String,
        orderId: String,
        nozzleId: String,
        command: PreAuthCommand,
    ): PreAuthResult {
        val token = oauthClient.getAccessToken()
        val url = "$baseUrl/direct-authorize-requests/authorize"

        var response = httpClient.post(url) {
            header("Authorization", "Bearer $token")
            contentType(ContentType.Application.Json)
            setBody(authBody)
        }

        // Handle 401 retry.
        if (response.status == HttpStatusCode.Unauthorized) {
            oauthClient.invalidateToken()
            val retryToken = oauthClient.getAccessToken()
            response = httpClient.post(url) {
                header("Authorization", "Bearer $retryToken")
                contentType(ContentType.Application.Json)
                setBody(authBody)
            }
        }

        // Handle 400 (nozzle not lifted) -> DECLINED.
        if (response.status == HttpStatusCode.BadRequest) {
            val errorBody = response.bodyAsText()
            AppLogger.w(TAG, "Authorize returned 400 for OrderId=$orderId: $errorBody")

            val errorResponse = tryDeserialize<PetroniteErrorResponse>(errorBody)
            return PreAuthResult(
                status = PreAuthResultStatus.DECLINED,
                correlationId = orderId,
                message = errorResponse?.message ?: "Nozzle not lifted or pump not ready",
            )
        }

        // Check for other HTTP errors.
        if (response.status.value !in 200..299) {
            val errorBody = response.bodyAsText()
            return PreAuthResult(
                status = PreAuthResultStatus.ERROR,
                correlationId = orderId,
                message = "Petronite authorize returned HTTP ${response.status.value}: $errorBody",
            )
        }

        val responseBody = response.bodyAsText()
        val authResponse = try {
            json.decodeFromString<PetroniteAuthorizeResponse>(responseBody)
        } catch (e: Exception) {
            return PreAuthResult(
                status = PreAuthResultStatus.ERROR,
                correlationId = orderId,
                message = "Failed to parse authorize response: ${e.message}",
            )
        }

        val accepted = authResponse.status.equals("AUTHORIZED", ignoreCase = true)

        if (accepted) {
            // Track in the active pre-auth map for correlation on webhook arrival.
            val activePreAuth = ActivePreAuth(
                orderId = authResponse.orderId,
                nozzleId = nozzleId,
                pumpNumber = command.pumpNumber,
                odooOrderId = command.odooOrderId,
                preAuthId = null,
                createdAtMillis = System.currentTimeMillis(),
            )
            activePreAuths[authResponse.orderId] = activePreAuth

            AppLogger.i(TAG, "Pump authorized: OrderId=${authResponse.orderId}, " +
                "AuthCode=${authResponse.authorizationCode} (pump ${command.pumpNumber})")
        }

        return PreAuthResult(
            status = if (accepted) PreAuthResultStatus.AUTHORIZED else PreAuthResultStatus.DECLINED,
            correlationId = authResponse.orderId,
            authorizationCode = authResponse.authorizationCode,
            message = authResponse.message,
        )
    }

    // -----------------------------------------------------------------------
    // IFccAdapter — cancelPreAuth (PN-3.3)
    // -----------------------------------------------------------------------

    /**
     * Cancel a pre-authorization via the IFccAdapter interface.
     * Delegates to the Petronite-specific cancel using fccCorrelationId (OrderId).
     */
    override suspend fun cancelPreAuth(command: CancelPreAuthCommand): Boolean {
        val correlationId = command.fccCorrelationId
        if (correlationId.isNullOrBlank()) {
            AppLogger.w(TAG, "Cannot cancel pre-auth: fccCorrelationId is required for Petronite")
            return false
        }
        return cancelPreAuthByOrderId(correlationId)
    }

    /**
     * Cancels a pre-authorization by posting to /{orderId}/cancel.
     * Idempotent: 404 (already cancelled or not found) is treated as success.
     * Removes the order from the active pre-auth map regardless of API outcome.
     *
     * @param fccCorrelationId The Petronite OrderId to cancel.
     * @return true if cancel succeeded or was idempotent (404), false otherwise.
     */
    suspend fun cancelPreAuthByOrderId(fccCorrelationId: String): Boolean {
        return try {
            // Always remove from active map, regardless of API outcome.
            activePreAuths.remove(fccCorrelationId)

            val token = oauthClient.getAccessToken()
            val url = "$baseUrl/direct-authorize-requests/$fccCorrelationId/cancel"

            var response = httpClient.post(url) {
                header("Authorization", "Bearer $token")
            }

            if (response.status.value in 200..299) {
                AppLogger.i(TAG, "Pre-auth cancelled: OrderId=$fccCorrelationId")
                return true
            }

            // 401 retry: invalidate and try once more.
            if (response.status == HttpStatusCode.Unauthorized) {
                oauthClient.invalidateToken()
                val retryToken = oauthClient.getAccessToken()
                response = httpClient.post(url) {
                    header("Authorization", "Bearer $retryToken")
                }
                if (response.status.value in 200..299) {
                    AppLogger.i(TAG, "Pre-auth cancelled (after 401 retry): OrderId=$fccCorrelationId")
                    return true
                }
            }

            // 404 = order not found or already terminal -- treat as idempotent success.
            if (response.status == HttpStatusCode.NotFound) {
                AppLogger.d(TAG, "Cancel returned 404 for $fccCorrelationId (already cancelled or not found)")
                return true
            }

            AppLogger.w(TAG, "Cancel pre-auth returned HTTP ${response.status.value} for $fccCorrelationId")
            false
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            AppLogger.w(TAG, "Cancel pre-auth error for $fccCorrelationId: ${e::class.simpleName}: ${e.message}")
            false
        }
    }

    // -----------------------------------------------------------------------
    // IFccAdapter -- heartbeat (PN-1.5)
    // -----------------------------------------------------------------------

    /**
     * Uses GET /nozzles/assigned as a liveness probe.
     * 5-second hard deadline. On 401: invalidates OAuth token and retries once.
     * Never throws -- returns true/false.
     */
    override suspend fun heartbeat(): Boolean {
        return try {
            withTimeout(HEARTBEAT_TIMEOUT_MS) {
                val success = tryHeartbeatOnce()
                if (success) return@withTimeout true

                // Retry once after invalidating the token (handles 401).
                AppLogger.d(TAG, "Heartbeat failed, invalidating token and retrying")
                oauthClient.invalidateToken()
                tryHeartbeatOnce()
            }
        } catch (e: TimeoutCancellationException) {
            AppLogger.d(TAG, "Heartbeat timed out after ${HEARTBEAT_TIMEOUT_MS}ms")
            false
        } catch (e: CancellationException) {
            throw e // Preserve structured concurrency cancellation
        } catch (e: Exception) {
            AppLogger.d(TAG, "Heartbeat failed: ${e::class.simpleName}: ${e.message}")
            false
        }
    }

    /**
     * Performs a single heartbeat attempt (GET /nozzles/assigned).
     * Returns true on 2xx, false otherwise.
     */
    private suspend fun tryHeartbeatOnce(): Boolean {
        return try {
            val token = oauthClient.getAccessToken()
            val url = "$baseUrl/nozzles/assigned"

            val response = httpClient.get(url) {
                header("Authorization", "Bearer $token")
            }
            response.status.value in 200..299
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            AppLogger.d(TAG, "Heartbeat attempt failed: ${e::class.simpleName}: ${e.message}")
            false
        }
    }

    // -----------------------------------------------------------------------
    // IFccAdapter -- acknowledgeTransactions (no-op)
    // -----------------------------------------------------------------------

    /**
     * No-op -- Petronite is push-only; there is no FCC buffer to acknowledge against.
     */
    override suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean = true

    // -----------------------------------------------------------------------
    // IFccAdapter -- getPumpStatus
    // -----------------------------------------------------------------------

    /**
     * Synthesizes pump status from nozzle assignments and cross-references
     * with active pre-auths for AUTHORIZED state.
     * Returns empty list on any error.
     */
    override suspend fun getPumpStatus(): List<PumpStatus> {
        return try {
            val assignments = nozzleResolver.getLastAssignments()
            if (assignments.isEmpty()) return emptyList()

            // Build a set of nozzleIds that have active pre-auths.
            val authorizedNozzles = mutableSetOf<String>()
            for (preAuth in activePreAuths.values) {
                authorizedNozzles.add(preAuth.nozzleId)
            }

            val now = Instant.now().toString()
            val result = mutableListOf<PumpStatus>()

            for (nozzle in assignments) {
                val state = if (nozzle.nozzleId in authorizedNozzles) {
                    PumpState.AUTHORIZED
                } else {
                    mapNozzleStatus(nozzle.status)
                }

                result.add(
                    PumpStatus(
                        siteCode = config.siteCode,
                        pumpNumber = nozzle.pumpNumber,
                        nozzleNumber = nozzle.nozzleNumber,
                        state = state,
                        currencyCode = config.currencyCode,
                        statusSequence = 0,
                        observedAtUtc = now,
                        source = PumpStatusSource.EDGE_SYNTHESIZED,
                        productCode = nozzle.productCode,
                        productName = nozzle.productName,
                        fccStatusCode = nozzle.status,
                    ),
                )
            }

            result
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            AppLogger.w(TAG, "getPumpStatus failed, returning empty list: ${e::class.simpleName}: ${e.message}")
            emptyList()
        }
    }

    // -----------------------------------------------------------------------
    // reconcileOnStartup (PN-3.4) -- Petronite-specific, not on IFccAdapter
    // -----------------------------------------------------------------------

    /**
     * Reconciles pending pre-authorizations on adapter startup.
     * Fetches GET /direct-authorize-requests/pending, then:
     *   - Orders older than 30 minutes: auto-cancelled
     *   - Recent orders: re-adopted into the active pre-auth map
     *
     * Non-fatal: logs errors and continues. Call this during service startup.
     */
    suspend fun reconcileOnStartup() {
        AppLogger.i(TAG, "Startup reconciliation: fetching pending orders...")

        val pendingOrders: List<PetronitePendingOrder>
        try {
            pendingOrders = getWithAuthRetry("/direct-authorize-requests/pending")
                ?: run {
                    AppLogger.i(TAG, "Startup reconciliation: no pending orders (null response)")
                    return
                }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            AppLogger.w(TAG, "Startup reconciliation: failed to fetch pending orders, skipping: ${e.message}")
            return
        }

        if (pendingOrders.isEmpty()) {
            AppLogger.i(TAG, "Startup reconciliation: no pending orders found")
            return
        }

        AppLogger.i(TAG, "Startup reconciliation: found ${pendingOrders.size} pending order(s)")

        val nowMillis = System.currentTimeMillis()
        var cancelled = 0
        var adopted = 0

        for (order in pendingOrders) {
            try {
                val createdAtMillis = parseTimestampToMillis(order.createdAt) ?: nowMillis
                val ageMillis = nowMillis - createdAtMillis

                if (ageMillis > STALE_ORDER_THRESHOLD_MS) {
                    // Stale order: auto-cancel.
                    AppLogger.i(TAG, "Reconciliation: cancelling stale order ${order.orderId} " +
                        "(created at ${order.createdAt})")
                    cancelPreAuthByOrderId(order.orderId)
                    cancelled++
                } else {
                    // Recent order: re-adopt into active pre-auth map.
                    var pumpNumber = 0
                    try {
                        val canonical = nozzleResolver.resolveCanonical(order.nozzleId)
                        pumpNumber = canonical.pumpNumber
                    } catch (e: Exception) {
                        AppLogger.d(TAG, "Reconciliation: could not resolve nozzle ${order.nozzleId}: ${e.message}")
                    }

                    val activePreAuth = ActivePreAuth(
                        orderId = order.orderId,
                        nozzleId = order.nozzleId,
                        pumpNumber = pumpNumber,
                        odooOrderId = null,
                        preAuthId = null,
                        createdAtMillis = createdAtMillis,
                    )
                    activePreAuths[order.orderId] = activePreAuth
                    adopted++

                    AppLogger.i(TAG, "Reconciliation: re-adopted order ${order.orderId} " +
                        "(created at ${order.createdAt}, pump $pumpNumber)")
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                AppLogger.w(TAG, "Reconciliation: error processing order ${order.orderId}, skipping: ${e.message}")
            }
        }

        AppLogger.i(TAG, "Startup reconciliation complete: $cancelled cancelled, $adopted adopted")
    }

    // -----------------------------------------------------------------------
    // Private -- HTTP helpers with 401 retry
    // -----------------------------------------------------------------------

    /**
     * Sends an authenticated POST with JSON body and deserializes the response.
     * On 401, invalidates the OAuth token and retries once.
     */
    private suspend inline fun <reified T> postWithAuthRetry(
        path: String,
        body: String,
    ): T? {
        val url = "$baseUrl$path"
        val token = oauthClient.getAccessToken()

        var response = httpClient.post(url) {
            header("Authorization", "Bearer $token")
            contentType(ContentType.Application.Json)
            setBody(body)
        }

        // 401 retry: invalidate token and retry once.
        if (response.status == HttpStatusCode.Unauthorized) {
            oauthClient.invalidateToken()
            val retryToken = oauthClient.getAccessToken()
            response = httpClient.post(url) {
                header("Authorization", "Bearer $retryToken")
                contentType(ContentType.Application.Json)
                setBody(body)
            }
        }

        if (response.status.value !in 200..299) {
            val statusCode = response.status.value
            val errorBody = response.bodyAsText()
            throw PetroniteHttpException(
                message = "Petronite $path returned HTTP $statusCode: $errorBody",
                statusCode = statusCode,
            )
        }

        val responseBody = response.bodyAsText()
        return try {
            json.decodeFromString<T>(responseBody)
        } catch (e: Exception) {
            AppLogger.w(TAG, "Failed to deserialize response from $path: ${e.message}")
            null
        }
    }

    /**
     * Sends an authenticated GET and deserializes the response.
     * On 401, invalidates the OAuth token and retries once.
     */
    private suspend inline fun <reified T> getWithAuthRetry(path: String): T? {
        val url = "$baseUrl$path"
        val token = oauthClient.getAccessToken()

        var response = httpClient.get(url) {
            header("Authorization", "Bearer $token")
        }

        // 401 retry: invalidate token and retry once.
        if (response.status == HttpStatusCode.Unauthorized) {
            oauthClient.invalidateToken()
            val retryToken = oauthClient.getAccessToken()
            response = httpClient.get(url) {
                header("Authorization", "Bearer $retryToken")
            }
        }

        if (response.status.value !in 200..299) {
            val statusCode = response.status.value
            val errorBody = response.bodyAsText()
            throw PetroniteHttpException(
                message = "Petronite $path returned HTTP $statusCode: $errorBody",
                statusCode = statusCode,
            )
        }

        val responseBody = response.bodyAsText()
        return try {
            json.decodeFromString<T>(responseBody)
        } catch (e: Exception) {
            AppLogger.w(TAG, "Failed to deserialize response from $path: ${e.message}")
            null
        }
    }

    // -----------------------------------------------------------------------
    // Private -- utility helpers
    // -----------------------------------------------------------------------

    /**
     * Tries to deserialize JSON, returning null on failure.
     */
    private inline fun <reified T> tryDeserialize(jsonStr: String): T? {
        return try {
            json.decodeFromString<T>(jsonStr)
        } catch (e: Exception) {
            null
        }
    }

    /**
     * Parses an ISO 8601 timestamp string to UTC ISO 8601 format.
     * Returns null if parsing fails.
     */
    private fun parseTimestamp(timestamp: String): String? {
        if (timestamp.isBlank()) return null
        return try {
            OffsetDateTime.parse(timestamp, DateTimeFormatter.ISO_OFFSET_DATE_TIME)
                .toInstant()
                .toString()
        } catch (e: Exception) {
            try {
                // Try parsing as Instant directly (e.g. "2024-01-15T10:30:00Z")
                Instant.parse(timestamp).toString()
            } catch (e2: Exception) {
                AppLogger.d(TAG, "Failed to parse timestamp '$timestamp': ${e2.message}")
                null
            }
        }
    }

    /**
     * Parses an ISO 8601 timestamp to epoch milliseconds.
     * Returns null if parsing fails.
     */
    private fun parseTimestampToMillis(timestamp: String): Long? {
        if (timestamp.isBlank()) return null
        return try {
            OffsetDateTime.parse(timestamp, DateTimeFormatter.ISO_OFFSET_DATE_TIME)
                .toInstant()
                .toEpochMilli()
        } catch (e: Exception) {
            try {
                Instant.parse(timestamp).toEpochMilli()
            } catch (e2: Exception) {
                null
            }
        }
    }

    /**
     * Maps Petronite nozzle status string to canonical [PumpState].
     */
    private fun mapNozzleStatus(status: String): PumpState = when (status.uppercase()) {
        "IDLE", "AVAILABLE" -> PumpState.IDLE
        "CALLING", "NOZZLE_LIFTED" -> PumpState.CALLING
        "DISPENSING" -> PumpState.DISPENSING
        "AUTHORIZED" -> PumpState.AUTHORIZED
        "COMPLETED" -> PumpState.COMPLETED
        "ERROR" -> PumpState.ERROR
        "OFFLINE" -> PumpState.OFFLINE
        "PAUSED" -> PumpState.PAUSED
        else -> PumpState.UNKNOWN
    }

    /**
     * Returns the currency factor (10^decimals) as BigDecimal for the given ISO 4217 currency code.
     * Uses integer exponentiation -- no floating point.
     */
    private fun getCurrencyFactor(currencyCode: String): BigDecimal {
        val decimals = getCurrencyDecimals(currencyCode)
        return BigDecimal.TEN.pow(decimals)
    }

    /**
     * Returns the number of decimal places for the given ISO 4217 currency code.
     * Defaults to 2 for unrecognized currencies.
     */
    private fun getCurrencyDecimals(currencyCode: String): Int = when (currencyCode.uppercase()) {
        "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND" -> 3
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF",
        "KRW", "PYG", "RWF", "UGX", "UYI", "VND",
        "VUV", "XAF", "XOF", "XPF" -> 0
        else -> 2
    }

    /** Returns the number of active pre-authorizations currently tracked. */
    val activePreAuthCount: Int
        get() = activePreAuths.size

    // -----------------------------------------------------------------------
    // Companion -- constants
    // -----------------------------------------------------------------------

    companion object {
        private const val TAG = "PetroniteAdapter"
        val VENDOR = FccVendor.PETRONITE
        const val ADAPTER_VERSION = "1.0.0"
        const val PROTOCOL = "REST_JSON"

        /** Returns true if this adapter has a working implementation. */
        const val IS_IMPLEMENTED = true

        /** Hard timeout for heartbeat probe (5 seconds). */
        const val HEARTBEAT_TIMEOUT_MS = 5_000L

        /** Hard timeout for pre-auth requests (15 seconds -- two HTTP round-trips). */
        const val PREAUTH_TIMEOUT_MS = 15_000L

        /**
         * Duration after which a pending order found during startup reconciliation
         * is considered stale and should be auto-cancelled rather than re-adopted.
         * 30 minutes in milliseconds.
         */
        const val STALE_ORDER_THRESHOLD_MS = 30L * 60 * 1000

        /** Volume conversion factor: 1 litre = 1,000,000 microlitres. */
        private val MICROLITRES_PER_LITRE = BigDecimal(1_000_000)
    }
}

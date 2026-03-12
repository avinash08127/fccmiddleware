package com.fccmiddleware.edge.adapter.petronite

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

// ---------------------------------------------------------------------------
// Petronite protocol DTOs — JSON structures for Petronite REST API
// ---------------------------------------------------------------------------

/**
 * Token response from POST /oauth/token.
 */
@Serializable
data class PetroniteTokenResponse(
    @SerialName("access_token") val accessToken: String,
    @SerialName("token_type") val tokenType: String,
    @SerialName("expires_in") val expiresIn: Int,
)

/**
 * Nozzle assignment from GET /nozzles/assigned.
 * Maps a Petronite nozzle ID to canonical pump/nozzle numbers.
 */
@Serializable
data class PetroniteNozzleAssignment(
    @SerialName("nozzleId") val nozzleId: String,
    @SerialName("pumpNumber") val pumpNumber: Int,
    @SerialName("nozzleNumber") val nozzleNumber: Int,
    @SerialName("productCode") val productCode: String,
    @SerialName("productName") val productName: String? = null,
    @SerialName("status") val status: String,
)

/**
 * Create order request body for POST /direct-authorize-requests/create.
 */
@Serializable
data class PetroniteCreateOrderRequest(
    @SerialName("nozzleId") val nozzleId: String,
    @SerialName("maxVolumeLitres") val maxVolumeLitres: String,
    @SerialName("maxAmountMajor") val maxAmountMajor: String,
    @SerialName("currency") val currency: String,
    @SerialName("externalReference") val externalReference: String? = null,
)

/**
 * Create order response from POST /direct-authorize-requests/create.
 */
@Serializable
data class PetroniteCreateOrderResponse(
    @SerialName("orderId") val orderId: String,
    @SerialName("status") val status: String,
    @SerialName("message") val message: String? = null,
)

/**
 * Authorize pump request for POST /direct-authorize-requests/authorize.
 */
@Serializable
data class PetroniteAuthorizeRequest(
    @SerialName("orderId") val orderId: String,
)

/**
 * Authorize pump response from POST /direct-authorize-requests/authorize.
 */
@Serializable
data class PetroniteAuthorizeResponse(
    @SerialName("orderId") val orderId: String,
    @SerialName("status") val status: String,
    @SerialName("authorizationCode") val authorizationCode: String? = null,
    @SerialName("message") val message: String? = null,
)

/**
 * Webhook payload (POST from Petronite to our webhook endpoint).
 */
@Serializable
data class PetroniteWebhookPayload(
    @SerialName("eventType") val eventType: String,
    @SerialName("transaction") val transaction: PetroniteTransactionData? = null,
    @SerialName("timestamp") val timestamp: String,
)

/**
 * Transaction data within a Petronite webhook payload.
 * Volume and amounts are in major units (decimal strings) -- the adapter converts
 * to canonical minor/micro units using BigDecimal.
 */
@Serializable
data class PetroniteTransactionData(
    @SerialName("orderId") val orderId: String,
    @SerialName("nozzleId") val nozzleId: String,
    @SerialName("pumpNumber") val pumpNumber: Int,
    @SerialName("nozzleNumber") val nozzleNumber: Int,
    @SerialName("productCode") val productCode: String,
    @SerialName("volumeLitres") val volumeLitres: String,
    @SerialName("amountMajor") val amountMajor: String,
    @SerialName("unitPrice") val unitPrice: String,
    @SerialName("currency") val currency: String,
    @SerialName("startTime") val startTime: String,
    @SerialName("endTime") val endTime: String,
    @SerialName("receiptCode") val receiptCode: String? = null,
    @SerialName("attendantId") val attendantId: String? = null,
    @SerialName("paymentMethod") val paymentMethod: String,
)

/**
 * Pending order from GET /direct-authorize-requests/pending.
 */
@Serializable
data class PetronitePendingOrder(
    @SerialName("orderId") val orderId: String,
    @SerialName("nozzleId") val nozzleId: String,
    @SerialName("status") val status: String,
    @SerialName("createdAt") val createdAt: String,
    @SerialName("maxVolumeLitres") val maxVolumeLitres: String? = null,
    @SerialName("maxAmountMajor") val maxAmountMajor: String? = null,
)

/**
 * Field-level validation error returned by the Petronite API.
 */
@Serializable
data class PetroniteFieldError(
    @SerialName("field") val field: String,
    @SerialName("message") val message: String,
)

/**
 * Error response wrapper returned by the Petronite API on non-2xx responses.
 */
@Serializable
data class PetroniteErrorResponse(
    @SerialName("errorCode") val errorCode: String,
    @SerialName("message") val message: String,
    @SerialName("errors") val errors: List<PetroniteFieldError>? = null,
)

/**
 * Internal record tracking an active pre-authorization for correlation with
 * incoming webhook transactions.
 */
data class ActivePreAuth(
    /** Petronite-assigned order ID. */
    val orderId: String,
    /** Petronite nozzle ID used for the pre-auth. */
    val nozzleId: String,
    /** Canonical pump number from the original PreAuthCommand. */
    val pumpNumber: Int,
    /** Odoo order ID for correlation, if provided. */
    val odooOrderId: String?,
    /** Pre-auth record ID from the original command, if provided. */
    val preAuthId: String?,
    /** When this pre-auth was created. UTC epoch millis. */
    val createdAtMillis: Long,
)

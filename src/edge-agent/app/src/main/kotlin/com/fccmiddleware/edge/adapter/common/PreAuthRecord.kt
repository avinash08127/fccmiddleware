package com.fccmiddleware.edge.adapter.common

import kotlinx.serialization.Serializable

/**
 * Domain model for a pre-authorization record.
 *
 * This is the canonical domain object — NOT the Room entity.
 * The Room entity lives in buffer/entity/PreAuthRecord.kt.
 *
 * Matches pre-auth-record.schema.json v1 field-for-field.
 * Money fields use Long minor units. Timestamps use ISO 8601 UTC strings.
 * UUIDs use String.
 *
 * Terminal states: COMPLETED, CANCELLED, EXPIRED, FAILED.
 */
@Serializable
data class PreAuthRecord(
    /** Middleware-generated UUID v4. Primary key. */
    val id: String,

    /** Site identifier. Part of dedup key with odooOrderId. */
    val siteCode: String,

    /** Odoo POS order reference. Part of dedup key with siteCode. */
    val odooOrderId: String,

    /** Target pump number. */
    val pumpNumber: Int,

    /** Target nozzle number. */
    val nozzleNumber: Int,

    /** Canonical fuel product code (e.g., PMS, AGO, IK, DPK). */
    val productCode: String,

    /** Authorized amount in integer minor units (cents). Always > 0. */
    val requestedAmount: Long,

    /** Price per litre at time of authorization, in minor units per litre. */
    val unitPrice: Long,

    /** ISO 4217 currency code (e.g., ZAR, KES, TZS). */
    val currency: String,

    /** Current pre-authorization lifecycle state. */
    val status: PreAuthStatus,

    /** When Odoo POS submitted the pre-auth request to the Edge Agent. UTC ISO 8601. */
    val requestedAt: String,

    /** requestedAt + preAuthTimeoutMinutes. UTC ISO 8601. */
    val expiresAt: String,

    /** Record creation timestamp in UTC ISO 8601. */
    val createdAt: String,

    /** Last modification timestamp. Updated on every state transition. UTC ISO 8601. */
    val updatedAt: String,

    /** Schema version for forward compatibility. Current value: 1. */
    val schemaVersion: Int,

    /** Vehicle registration plate, if captured. */
    val vehicleNumber: String? = null,

    /** Customer name. Required when requireCustomerTaxId=true. */
    val customerName: String? = null,

    /** Customer Tax Identification Number (TIN). PII — NEVER log. */
    val customerTaxId: String? = null,

    /** B2B business name. Required for B2B fiscalized transactions. */
    val customerBusinessName: String? = null,

    /** Odoo user ID of the fuel attendant. */
    val attendantId: String? = null,

    /** FCC-assigned correlation reference returned at authorization time. */
    val fccCorrelationId: String? = null,

    /** FCC authorization confirmation code. Null until AUTHORIZED state. */
    val fccAuthorizationCode: String? = null,

    /** fccTransactionId of the final dispense matched by the reconciliation engine. Null until COMPLETED. */
    val matchedFccTransactionId: String? = null,

    /** Actual dispensed amount in integer minor units. Null until COMPLETED. */
    val actualAmount: Long? = null,

    /** Actual dispensed volume in integer millilitres (1 litre = 1000). Null until COMPLETED. */
    val actualVolume: Long? = null,

    /** actualAmount - requestedAmount in minor units. Null on Edge Agent. */
    val amountVariance: Long? = null,

    /** ABS(amountVariance) / requestedAmount * 10000, rounded (basis points). Null on Edge Agent. */
    val varianceBps: Long? = null,

    /** When FCC returned authorization confirmation. Null until AUTHORIZED. UTC ISO 8601. */
    val authorizedAt: String? = null,

    /** When FCC signalled dispense started. Null if vendor does not report DISPENSING. UTC ISO 8601. */
    val dispensingAt: String? = null,

    /** When reconciliation engine matched the final dispense. Null until COMPLETED. UTC ISO 8601. */
    val completedAt: String? = null,

    /** When cancellation was processed. Null unless CANCELLED. UTC ISO 8601. */
    val cancelledAt: String? = null,

    /** When expiry worker transitioned the record. Null unless EXPIRED. UTC ISO 8601. */
    val expiredAt: String? = null,

    /** When FCC rejection was processed. Null unless FAILED. UTC ISO 8601. */
    val failedAt: String? = null,
)

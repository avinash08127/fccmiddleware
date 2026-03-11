package com.fccmiddleware.edge.adapter.common

import kotlinx.serialization.Serializable

/**
 * Canonical, vendor-neutral representation of a fuel dispensing transaction.
 *
 * Matches canonical-transaction.schema.json v1 field-for-field.
 * Money fields use Long minor units. Timestamps use ISO 8601 UTC strings.
 * UUIDs use String.
 */
@Serializable
data class CanonicalTransaction(
    /** Middleware-generated primary key (UUID v4). */
    val id: String,

    /** Opaque transaction ID from the FCC. Dedup key with siteCode. */
    val fccTransactionId: String,

    /** Site where dispense occurred. Dedup key with fccTransactionId. */
    val siteCode: String,

    /** Physical pump number (after pumpNumberOffset applied). */
    val pumpNumber: Int,

    /** Physical nozzle number on the pump. */
    val nozzleNumber: Int,

    /** Canonical product code after mapping (e.g., PMS, AGO, IK, UNKNOWN). */
    val productCode: String,

    /** Dispensed volume in microlitres (1 L = 1,000,000 µL). */
    val volumeMicrolitres: Long,

    /** Total transaction amount in minor currency units (e.g., cents). */
    val amountMinorUnits: Long,

    /** Price per litre in minor currency units. */
    val unitPriceMinorPerLitre: Long,

    /** Dispense start time in UTC (ISO 8601). */
    val startedAt: String,

    /** Dispense completion time in UTC (ISO 8601). Must be >= startedAt. */
    val completedAt: String,

    /** FCC vendor that produced this transaction. */
    val fccVendor: FccVendor,

    /** Legal entity owning the site. Denormalised for row-level scoping. */
    val legalEntityId: String,

    /** ISO 4217 currency code for monetary fields on this transaction. */
    val currencyCode: String,

    /** Current lifecycle status of the transaction. */
    val status: TransactionStatus,

    /** Which ingestion path delivered this transaction. */
    val ingestionSource: IngestionSource,

    /** UTC timestamp when middleware first received and persisted this transaction. */
    val ingestedAt: String,

    /** UTC timestamp of last status change. Must be >= ingestedAt. */
    val updatedAt: String,

    /** Version of the canonical model schema used. Current value: 1. */
    val schemaVersion: Int,

    /** Whether flagged as potential duplicate by secondary dedup check. */
    val isDuplicate: Boolean,

    /** Trace correlation ID for observability. */
    val correlationId: String,

    /** Fiscal receipt reference if FCC fiscalizes directly. Null otherwise. */
    val fiscalReceiptNumber: String? = null,

    /** Attendant/operator identifier from FCC. Null if not captured. */
    val attendantId: String? = null,

    /** S3 URI referencing the archived raw FCC payload. Null on Edge Agent. */
    val rawPayloadRef: String? = null,

    /** Inline raw FCC payload JSON. Used on Edge Agent. Null on Cloud. */
    val rawPayloadJson: String? = null,

    /** Odoo order reference stamped when Odoo acknowledges. Null until acknowledged. */
    val odooOrderId: String? = null,

    /** FK to PreAuthRecord when matched via reconciliation. */
    val preAuthId: String? = null,

    /** Reconciliation outcome. Null for normal orders at non-pre-auth sites. */
    val reconciliationStatus: ReconciliationStatus? = null,

    /**
     * If isDuplicate=true, references the original transaction's id.
     * Must be non-null when isDuplicate=true.
     */
    val duplicateOfId: String? = null,
)

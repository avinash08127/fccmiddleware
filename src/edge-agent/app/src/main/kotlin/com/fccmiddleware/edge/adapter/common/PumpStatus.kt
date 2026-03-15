package com.fccmiddleware.edge.adapter.common

import kotlinx.serialization.Serializable

/**
 * Real-time pump state snapshot from the FCC.
 *
 * Matches pump-status.schema.json v1.0 field-for-field.
 * In-flight decimal amounts (volume, price) are represented as formatted strings
 * per the schema — they are display values, not arithmetic operands.
 * All monetary arithmetic uses Long minor units elsewhere.
 */
@Serializable
data class PumpStatus(
    /** Schema version. Always "1.0". */
    val schemaVersion: String = "1.0",

    /** Site identifier. */
    val siteCode: String,

    /** Physical pump number. */
    val pumpNumber: Int,

    /** Physical nozzle number. */
    val nozzleNumber: Int,

    /** Current operational state of the pump-nozzle pair. */
    val state: PumpState,

    /** ISO 4217 currency code. */
    val currencyCode: String,

    /** Monotonically increasing sequence number for ordering concurrent status updates. */
    val statusSequence: Int,

    /** When this status was observed. UTC ISO 8601 with trailing Z. */
    val observedAtUtc: String,

    /** Whether this reading came directly from the FCC or was synthesised by the Edge Agent. */
    val source: PumpStatusSource,

    /** Canonical product code currently on the nozzle. Null when idle or unknown. */
    val productCode: String? = null,

    /** Human-readable product name. Null when idle or unknown. */
    val productName: String? = null,

    /**
     * In-progress dispensed volume in litres as a decimal string (e.g. "12.345").
     * Null when not dispensing. Pattern: ^(0|[1-9][0-9]*)(\.[0-9]{1,3})?$
     */
    val currentVolumeLitres: String? = null,

    /**
     * In-progress dispensed amount as a decimal string (e.g. "152.50").
     * Null when not dispensing. Pattern: ^(0|[1-9][0-9]*)(\.[0-9]{1,2})?$
     */
    val currentAmount: String? = null,

    /**
     * Unit price per litre as a decimal string (e.g. "12.3400").
     * Null when not available. Pattern: ^(0|[1-9][0-9]*)(\.[0-9]{1,4})?$
     */
    val unitPrice: String? = null,

    /** Vendor-native status code for diagnostics. Null if not exposed by FCC. */
    val fccStatusCode: String? = null,

    /** When the pump state last changed. Null if vendor does not report. UTC ISO 8601 with trailing Z. */
    val lastChangedAtUtc: String? = null,

    /** Extended supplemental status from FpStatus_3. Null when not included. */
    val supplemental: PumpStatusSupplemental? = null,
)

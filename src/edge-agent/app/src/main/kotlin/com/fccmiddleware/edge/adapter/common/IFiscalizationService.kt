package com.fccmiddleware.edge.adapter.common

import java.math.BigDecimal

/**
 * Fiscalization service for post-dispense fiscal receipt generation (ADV-7.1).
 *
 * Used in Scenario A where the primary FCC (DOMS/Radix) controls pumps and provides
 * transaction data, and a secondary fiscal device (e.g. Advatec EFD) generates
 * TRA-compliant fiscal receipts after each dispense.
 *
 * The flow is:
 *   1. Primary FCC transaction is ingested and buffered by [IngestionOrchestrator]
 *   2. This service POSTs customer/transaction data to the fiscal device
 *   3. The fiscal device generates a fiscal receipt and returns it via webhook
 *   4. The fiscal receipt data is attached to the original buffered transaction
 */
interface IFiscalizationService {

    /**
     * Submits a completed transaction for fiscal receipt generation.
     * Posts customer data to the fiscal device and awaits the receipt webhook.
     *
     * @param transaction The buffered canonical transaction to fiscalize.
     * @param context Customer and payment context for the fiscal receipt.
     * @return Result containing the fiscal receipt code and TRA verification URL on success.
     */
    suspend fun submitForFiscalization(
        transaction: CanonicalTransaction,
        context: FiscalizationContext,
    ): FiscalizationResult

    /**
     * Checks if the fiscal device is reachable (TCP connect or health endpoint).
     * Never throws — returns false on any failure.
     */
    suspend fun isAvailable(): Boolean

    /**
     * Shuts down the service and releases resources (webhook listener, HTTP client).
     */
    fun shutdown()
}

/**
 * Customer and payment context required for fiscal receipt generation.
 * Populated from the pre-auth record or site-level defaults.
 */
data class FiscalizationContext(
    /** Customer TIN for TRA fiscal receipts. PII — never log. */
    val customerTaxId: String?,
    /** Customer display name on the fiscal receipt. */
    val customerName: String?,
    /** TRA CustIdType (1=TIN, 2=DL, 3=Voters, 4=Passport, 5=NID, 6=NIL). */
    val customerIdType: Int?,
    /** Payment method for the fiscal receipt (CASH, CCARD, EMONEY, INVOICE, CHEQUE). */
    val paymentType: String?,
)

/**
 * Result of a fiscalization attempt.
 */
data class FiscalizationResult(
    /** Whether the fiscal receipt was successfully generated. */
    val success: Boolean,
    /** TRA fiscal receipt code (e.g. "9a8b7c6d5e1"). Null on failure. */
    val receiptCode: String? = null,
    /** TRA verification URL. Null on failure. */
    val receiptVCodeUrl: String? = null,
    /** Total tax amount from the fiscal receipt. Null on failure. */
    val totalTaxAmount: BigDecimal? = null,
    /** Error description when [success] is false. */
    val errorMessage: String? = null,
)

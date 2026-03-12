package com.fccmiddleware.edge.adapter.doms.model

import kotlinx.serialization.Serializable

/** Raw DOMS transaction data from the supervised buffer (FpSupTrans response). */
@Serializable
data class DomsTransactionDto(
    val transactionId: String,
    val fpId: Int,
    val nozzleId: Int,
    val productCode: String,
    /** Volume in centilitres (integer). */
    val volumeCl: Long,
    /** Amount in DOMS units (x10 of minor currency units). */
    val amountX10: Long,
    /** Unit price in DOMS units. */
    val unitPriceX10: Long,
    /** Timestamp in "yyyyMMddHHmmss" format, local time. */
    val timestamp: String,
    val attendantId: String? = null,
    val bufferIndex: Int,
)

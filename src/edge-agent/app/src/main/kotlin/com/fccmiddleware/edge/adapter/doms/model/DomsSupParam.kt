package com.fccmiddleware.edge.adapter.doms.model

import kotlinx.serialization.Serializable

/** DOMS supervised transaction parameters -- wraps the data fields of FpSupTrans. */
@Serializable
data class DomsSupParam(
    val fpId: Int,
    val transId: String,
    val nozzleId: Int,
    val productCode: String,
    val volumeCl: Long,
    val amountX10: Long,
    val unitPriceX10: Long,
    val timestamp: String,
    val attendantId: String? = null,
)

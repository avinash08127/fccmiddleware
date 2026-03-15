package com.fccmiddleware.edge.replication

import kotlinx.serialization.Serializable

// ── Snapshot (full bootstrap for new standby) ───────────────────────────────

@Serializable
data class SnapshotPayload(
    val primaryAgentId: String,
    val epoch: Long,
    val configVersion: String? = null,
    val highWaterMarkTxSeq: Long = 0,
    val highWaterMarkPaSeq: Long = 0,
    val transactions: List<ReplicatedTransaction> = emptyList(),
    val preAuths: List<ReplicatedPreAuth> = emptyList(),
    val nozzles: List<ReplicatedNozzle> = emptyList(),
    val generatedAt: String = "",
)

// ── Delta (incremental sync) ────────────────────────────────────────────────

@Serializable
data class DeltaSyncPayload(
    val primaryAgentId: String,
    val epoch: Long,
    val fromSeq: Long,
    val toSeq: Long,
    val transactions: List<ReplicatedTransaction> = emptyList(),
    val preAuths: List<ReplicatedPreAuth> = emptyList(),
    val hasMore: Boolean = false,
    val generatedAt: String = "",
)

// ── Replicated entity DTOs ──────────────────────────────────────────────────

@Serializable
data class ReplicatedTransaction(
    val id: String = "",
    val fccTransactionId: String = "",
    val siteCode: String = "",
    val pumpNumber: Int = 0,
    val nozzleNumber: Int = 0,
    val productCode: String = "",
    val volumeMicrolitres: Long = 0,
    val amountMinorUnits: Long = 0,
    val unitPriceMinorPerLitre: Long = 0,
    val currencyCode: String = "",
    val startedAt: String = "",
    val completedAt: String = "",
    val fiscalReceiptNumber: String? = null,
    val fccVendor: String = "",
    val attendantId: String? = null,
    val status: String = "",
    val syncStatus: String = "",
    val ingestionSource: String = "",
    val correlationId: String? = null,
    val preAuthId: String? = null,
    val replicationSeq: Long = 0,
    val sourceAgentId: String = "",
    val createdAt: String = "",
    val updatedAt: String = "",
)

@Serializable
data class ReplicatedPreAuth(
    val id: String = "",
    val siteCode: String = "",
    val odooOrderId: String = "",
    val pumpNumber: Int = 0,
    val nozzleNumber: Int = 0,
    val productCode: String = "",
    val requestedAmount: Long = 0,
    val unitPrice: Long = 0,
    val currency: String = "",
    val status: String = "",
    val requestedAt: String = "",
    val expiresAt: String = "",
    val fccCorrelationId: String? = null,
    val fccAuthorizationCode: String? = null,
    val replicationSeq: Long = 0,
    val sourceAgentId: String = "",
    val createdAt: String = "",
    val updatedAt: String = "",
)

@Serializable
data class ReplicatedNozzle(
    val id: String = "",
    val siteCode: String = "",
    val fccPumpNumber: Int = 0,
    val fccNozzleNumber: Int = 0,
    val odooPumpNumber: Int = 0,
    val odooNozzleNumber: Int = 0,
    val productCode: String = "",
)

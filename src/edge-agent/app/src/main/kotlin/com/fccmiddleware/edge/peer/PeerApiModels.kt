package com.fccmiddleware.edge.peer

import kotlinx.serialization.Serializable

// ── Heartbeat ───────────────────────────────────────────────────────────────

@Serializable
data class PeerHeartbeatRequest(
    val agentId: String,
    val siteCode: String,
    val currentRole: String,
    val leaderEpoch: Long,
    val leaderAgentId: String? = null,
    val configVersion: String? = null,
    val replicationLagSeconds: Double = 0.0,
    val lastSequenceApplied: Long = 0,
    val deviceClass: String,
    val appVersion: String,
    val uptimeSeconds: Long,
    val sentAtUtc: String,
)

@Serializable
data class PeerHeartbeatResponse(
    val agentId: String,
    val currentRole: String,
    val leaderEpoch: Long,
    val accepted: Boolean,
    val receivedAtUtc: String,
)

// ── Health ──────────────────────────────────────────────────────────────────

@Serializable
data class PeerHealthResponse(
    val agentId: String,
    val siteCode: String,
    val currentRole: String,
    val leaderEpoch: Long,
    val fccReachable: Boolean,
    val uptimeSeconds: Long,
    val appVersion: String,
    val highWaterMarkSeq: Long,
    val reportedAtUtc: String,
)

// ── Leadership Claim ────────────────────────────────────────────────────────

@Serializable
data class PeerLeadershipClaimRequest(
    val candidateAgentId: String,
    val proposedEpoch: Long,
    val priority: Int,
    val siteCode: String,
)

@Serializable
data class PeerLeadershipClaimResponse(
    val accepted: Boolean,
    val reason: String? = null,
    val currentEpoch: Long,
)

// ── Pre-Auth Proxy ──────────────────────────────────────────────────────────

@Serializable
data class PeerProxyPreAuthRequest(
    val pumpNumber: Int,
    val nozzleNumber: Int,
    val productCode: String,
    val requestedAmount: Long,
    val unitPrice: Long,
    val currency: String,
    val odooOrderId: String,
    val vehicleNumber: String? = null,
    val customerName: String? = null,
    val customerTaxId: String? = null,
    val customerBusinessName: String? = null,
    val attendantId: String? = null,
    val correlationId: String? = null,
)

@Serializable
data class PeerProxyPreAuthResponse(
    val success: Boolean,
    val preAuthId: String? = null,
    val fccCorrelationId: String? = null,
    val fccAuthorizationCode: String? = null,
    val failureReason: String? = null,
    val status: String,
)

// ── Pump Status Proxy ───────────────────────────────────────────────────────

@Serializable
data class PeerProxyPumpStatusResponse(
    val pumps: List<PeerPumpStatus>,
)

@Serializable
data class PeerPumpStatus(
    val pumpNumber: Int,
    val status: String,
    val currentNozzle: Int? = null,
    val currentProductCode: String? = null,
)

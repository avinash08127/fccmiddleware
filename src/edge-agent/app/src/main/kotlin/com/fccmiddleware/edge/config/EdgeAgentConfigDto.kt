package com.fccmiddleware.edge.config

import kotlinx.serialization.Serializable

/**
 * Kotlin DTO mirroring the EdgeAgentConfig JSON schema (v2.0).
 *
 * Used by [ConfigPollWorker] to deserialize the cloud response and by
 * [ConfigManager] for in-memory config access.
 *
 * Unknown fields are ignored by the parser (`ignoreUnknownKeys = true`)
 * per the backward-compatibility rules in §5.3.
 */
@Serializable
data class EdgeAgentConfigDto(
    val schemaVersion: String,
    val configVersion: Int,
    val configId: String,
    val issuedAtUtc: String,
    val effectiveAtUtc: String,
    val compatibility: CompatibilityDto,
    val agent: AgentDto,
    val site: SiteDto,
    val fccConnection: FccConnectionDto,
    val polling: PollingDto,
    val sync: SyncDto,
    val buffer: BufferDto,
    val api: ApiDto,
    val telemetry: TelemetryDto,
    val fiscalization: FiscalizationDto,
)

@Serializable
data class CompatibilityDto(
    val minAgentVersion: String,
    val maxAgentVersion: String? = null,
)

@Serializable
data class AgentDto(
    val deviceId: String,
    val isPrimaryAgent: Boolean = true,
)

@Serializable
data class SiteDto(
    val siteCode: String,
    val legalEntityId: String,
    val timezone: String,
    val currency: String,
    val operatingModel: String,
    val connectivityMode: String,
)

@Serializable
data class FccConnectionDto(
    val vendor: String,
    val host: String,
    val port: Int,
    val credentialsRef: String,
    val protocolType: String = "REST",
    val transactionMode: String = "PULL",
    val ingestionMode: String = "CLOUD_DIRECT",
    val heartbeatIntervalSeconds: Int = 15,
)

@Serializable
data class PollingDto(
    val pullIntervalSeconds: Int = 30,
    val batchSize: Int = 100,
    val cursorStrategy: String = "LAST_SUCCESSFUL_TIMESTAMP",
)

@Serializable
data class SyncDto(
    val cloudBaseUrl: String,
    val uploadBatchSize: Int = 50,
    val syncIntervalSeconds: Int = 30,
    val statusPollIntervalSeconds: Int = 30,
    val configPollIntervalSeconds: Int = 60,
)

@Serializable
data class BufferDto(
    val retentionDays: Int = 30,
    val maxRecords: Int = 50000,
    val cleanupIntervalHours: Int = 24,
)

@Serializable
data class ApiDto(
    val localApiPort: Int = 8585,
    val enableLanApi: Boolean = false,
    val lanApiKeyRef: String? = null,
)

@Serializable
data class TelemetryDto(
    val telemetryIntervalSeconds: Int = 60,
    val logLevel: String = "INFO",
)

@Serializable
data class FiscalizationDto(
    val mode: String,
    val requireCustomerTaxId: Boolean = false,
    val fiscalReceiptRequired: Boolean = false,
)

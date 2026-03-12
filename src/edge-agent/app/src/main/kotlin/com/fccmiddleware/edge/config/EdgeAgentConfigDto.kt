package com.fccmiddleware.edge.config

import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import com.fccmiddleware.edge.adapter.common.FccVendor
import com.fccmiddleware.edge.adapter.common.IngestionMode
import com.fccmiddleware.edge.api.LocalApiServer
import kotlinx.serialization.Serializable
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlin.math.max

/**
 * Canonical Android representation of the cloud [SiteConfigResponse] payload.
 *
 * Field names intentionally match the JSON emitted by the cloud API so config
 * polling, registration bootstrap, and local persistence all use one contract.
 */
@Serializable
data class EdgeAgentConfigDto(
    val schemaVersion: String,
    val configVersion: Int,
    val configId: String,
    val issuedAtUtc: String,
    val effectiveAtUtc: String,
    val sourceRevision: SourceRevisionDto,
    val identity: IdentityDto,
    val site: SiteDto,
    val fcc: FccDto,
    val sync: SyncDto,
    val buffer: BufferDto,
    val localApi: LocalApiDto,
    val telemetry: TelemetryDto,
    val fiscalization: FiscalizationDto,
    val mappings: MappingsDto,
    val rollout: RolloutDto,
)

@Serializable
data class SourceRevisionDto(
    val databricksSyncAtUtc: String? = null,
    val siteMasterRevision: String? = null,
    val fccConfigRevision: String? = null,
    val portalChangeId: String? = null,
)

@Serializable
data class IdentityDto(
    val legalEntityId: String,
    val legalEntityCode: String,
    val siteId: String,
    val siteCode: String,
    val siteName: String,
    val timezone: String,
    val currencyCode: String,
    val deviceId: String,
    val isPrimaryAgent: Boolean = true,
)

@Serializable
data class SiteDto(
    val isActive: Boolean,
    val operatingModel: String,
    val siteUsesPreAuth: Boolean,
    val connectivityMode: String,
    val odooSiteId: String,
    val companyTaxPayerId: String,
    val operatorName: String? = null,
    val operatorTaxPayerId: String? = null,
)

@Serializable
data class FccDto(
    val enabled: Boolean,
    val fccId: String? = null,
    val vendor: String? = null,
    val model: String? = null,
    val version: String? = null,
    val connectionProtocol: String? = null,
    val hostAddress: String? = null,
    val port: Int? = null,
    val credentialRef: String? = null,
    val credentialRevision: Int? = null,
    val secretEnvelope: SecretEnvelopeDto = SecretEnvelopeDto(format = "NONE"),
    val transactionMode: String? = null,
    val ingestionMode: String? = null,
    val pullIntervalSeconds: Int? = null,
    val catchUpPullIntervalSeconds: Int? = null,
    val hybridCatchUpIntervalSeconds: Int? = null,
    val heartbeatIntervalSeconds: Int = 15,
    val heartbeatTimeoutSeconds: Int = 45,
    val pushSourceIpAllowList: List<String> = emptyList(),
)

@Serializable
data class SecretEnvelopeDto(
    val format: String,
    val payload: String? = null,
)

@Serializable
data class SyncDto(
    val cloudBaseUrl: String,
    val uploadBatchSize: Int = 50,
    val uploadIntervalSeconds: Int = 30,
    val syncedStatusPollIntervalSeconds: Int = 30,
    val configPollIntervalSeconds: Int = 60,
    val cursorStrategy: String = "LAST_SUCCESSFUL_TIMESTAMP",
    val maxReplayBackoffSeconds: Int = 300,
    val initialReplayBackoffSeconds: Int = 5,
    val maxRecordsPerUploadWindow: Int = 5000,
    /**
     * Runtime certificate pins (SHA-256 public key hashes) for TLS certificate pinning.
     * Format: "sha256/BASE64HASH=". When non-empty, agents prefer these over bootstrap
     * pins bundled in the APK. Enables pin rotation without APK update.
     * Per security spec §5.3: 30-day overlap window for graceful rotation.
     * Applied on next app restart (OkHttp CertificatePinner is immutable after construction).
     */
    val certificatePins: List<String> = emptyList(),
)

@Serializable
data class BufferDto(
    val retentionDays: Int = 30,
    val stalePendingDays: Int = 3,
    val maxRecords: Int = 50000,
    val cleanupIntervalHours: Int = 24,
    val persistRawPayloads: Boolean = false,
)

@Serializable
data class LocalApiDto(
    val localhostPort: Int = 8585,
    val enableLanApi: Boolean = false,
    val lanBindAddress: String? = null,
    val lanAllowCidrs: List<String> = emptyList(),
    val lanApiKeyRef: String? = null,
    val rateLimitPerMinute: Int = 120,
)

@Serializable
data class TelemetryDto(
    val telemetryIntervalSeconds: Int = 60,
    val logLevel: String = "INFO",
    val includeDiagnosticsLogs: Boolean = false,
    val metricsWindowSeconds: Int = 300,
)

@Serializable
data class FiscalizationDto(
    val mode: String,
    val taxAuthorityEndpoint: String? = null,
    val requireCustomerTaxId: Boolean = false,
    val fiscalReceiptRequired: Boolean = false,
)

@Serializable
data class MappingsDto(
    val pumpNumberOffset: Int = 0,
    val priceDecimalPlaces: Int = 2,
    val volumeUnit: String = "LITRE",
    val products: List<ProductMappingDto> = emptyList(),
    val nozzles: List<NozzleMappingDto> = emptyList(),
)

@Serializable
data class ProductMappingDto(
    val fccProductCode: String,
    val canonicalProductCode: String,
    val displayName: String,
    val active: Boolean,
)

@Serializable
data class NozzleMappingDto(
    val odooPumpNumber: Int,
    val fccPumpNumber: Int,
    val odooNozzleNumber: Int,
    val fccNozzleNumber: Int,
    val productCode: String,
)

@Serializable
data class RolloutDto(
    val minAgentVersion: String,
    val maxAgentVersion: String? = null,
    val requiresRestartSections: List<String> = emptyList(),
    val configTtlHours: Int = 24,
)

object EdgeAgentConfigJson {
    val codec: Json = Json {
        ignoreUnknownKeys = true
        isLenient = true
        encodeDefaults = true
    }

    fun decode(rawJson: String): EdgeAgentConfigDto = codec.decodeFromString(rawJson)

    fun encode(config: EdgeAgentConfigDto): String = codec.encodeToString(config)
}

fun EdgeAgentConfigDto.requiresFccRuntime(): Boolean = site.isActive && fcc.enabled

fun EdgeAgentConfigDto.toAgentFccConfig(): AgentFccConfig {
    val vendor = requireNotNull(fcc.vendor) { "fcc.vendor is required when FCC is enabled" }
    val protocol = requireNotNull(fcc.connectionProtocol) {
        "fcc.connectionProtocol is required when FCC is enabled"
    }
    val host = requireNotNull(fcc.hostAddress) { "fcc.hostAddress is required when FCC is enabled" }
    val port = requireNotNull(fcc.port) { "fcc.port is required when FCC is enabled" }

    val ingestionMode = fcc.ingestionMode
        ?.let { IngestionMode.valueOf(it.uppercase()) }
        ?: IngestionMode.CLOUD_DIRECT

    return AgentFccConfig(
        fccVendor = FccVendor.valueOf(vendor.uppercase()),
        connectionProtocol = protocol,
        hostAddress = host,
        port = port,
        authCredential = resolvedFccCredential(),
        ingestionMode = ingestionMode,
        pullIntervalSeconds = fcc.pullIntervalSeconds
            ?: fcc.catchUpPullIntervalSeconds
            ?: fcc.hybridCatchUpIntervalSeconds
            ?: 30,
        productCodeMapping = mappings.products
            .filter { it.active }
            .associate { it.fccProductCode to it.canonicalProductCode },
        timezone = identity.timezone,
        currencyCode = identity.currencyCode,
        pumpNumberOffset = mappings.pumpNumberOffset,
        heartbeatIntervalSeconds = fcc.heartbeatIntervalSeconds,
    )
}

fun EdgeAgentConfigDto.toLocalApiServerConfig(
    lanApiKey: String? = null,
): LocalApiServer.LocalApiServerConfig {
    val perSecondLimit = max(1, (localApi.rateLimitPerMinute + 59) / 60)
    return LocalApiServer.LocalApiServerConfig(
        port = localApi.localhostPort,
        enableLanApi = localApi.enableLanApi,
        lanApiKey = lanApiKey,
        rateLimitMutatingRps = perSecondLimit,
        rateLimitReadRps = perSecondLimit,
    )
}

fun EdgeAgentConfigDto.resolvedFccCredential(): String =
    when {
        !fcc.secretEnvelope.payload.isNullOrBlank() -> fcc.secretEnvelope.payload
        !fcc.credentialRef.isNullOrBlank() -> fcc.credentialRef
        else -> ""
    }

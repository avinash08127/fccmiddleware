package com.fccmiddleware.edge.config

import android.util.Log
import com.fccmiddleware.edge.buffer.dao.AgentConfigDao
import com.fccmiddleware.edge.buffer.entity.AgentConfig
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.json.Json
import java.time.Instant

/**
 * ConfigManager — manages runtime configuration received from cloud.
 *
 * Responsibilities:
 * - Holds the current [EdgeAgentConfigDto] in memory as a [StateFlow].
 * - Persists config snapshots to Room via [AgentConfigDao].
 * - Loads last-known-good config from Room on startup (offline bootstrap).
 * - Validates incoming configs: schema version, config version ordering,
 *   provisioning-only field immutability.
 * - Classifies changed fields as hot-reload vs restart-required and logs
 *   warnings for restart-required changes (does not force restart).
 *
 * Thread-safe: all mutable state is behind [MutableStateFlow] or synchronized.
 */
class ConfigManager(
    private val agentConfigDao: AgentConfigDao,
) {

    companion object {
        private const val TAG = "ConfigManager"
        private const val SUPPORTED_MAJOR_VERSION = "2"
    }

    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
    }

    private val _config = MutableStateFlow<EdgeAgentConfigDto?>(null)

    /** Observable current config. Null until first config is loaded from Room or cloud. */
    val config: StateFlow<EdgeAgentConfigDto?> = _config.asStateFlow()

    /** Current config version, or null if no config has been applied. */
    val currentConfigVersion: Int?
        get() = _config.value?.configVersion

    /**
     * Load the last-known-good config from Room into memory.
     * Called once at startup before any cloud poll.
     */
    suspend fun loadFromLocal() {
        try {
            val stored = agentConfigDao.get() ?: run {
                Log.i(TAG, "No stored config found — awaiting first cloud config push")
                return
            }
            val parsed = json.decodeFromString<EdgeAgentConfigDto>(stored.configJson)
            _config.value = parsed
            Log.i(
                TAG,
                "Loaded config from local: version=${parsed.configVersion}, " +
                    "configId=${parsed.configId}",
            )
        } catch (e: Exception) {
            Log.e(TAG, "Failed to load config from local storage", e)
        }
    }

    /**
     * Apply a new config snapshot received from cloud.
     *
     * Returns a [ConfigApplyResult] indicating success, skip, or rejection.
     */
    suspend fun applyConfig(newConfig: EdgeAgentConfigDto, rawJson: String): ConfigApplyResult {
        // 1. Schema version compatibility check
        val majorVersion = newConfig.schemaVersion.substringBefore(".")
        if (majorVersion != SUPPORTED_MAJOR_VERSION) {
            Log.w(
                TAG,
                "Incompatible schema version: ${newConfig.schemaVersion} " +
                    "(supported major: $SUPPORTED_MAJOR_VERSION)",
            )
            return ConfigApplyResult.Rejected("INCOMPATIBLE_SCHEMA_VERSION")
        }

        // 2. Config version ordering — only apply if strictly greater
        val currentVersion = _config.value?.configVersion
        if (currentVersion != null && newConfig.configVersion <= currentVersion) {
            Log.d(
                TAG,
                "Config version ${newConfig.configVersion} <= current $currentVersion — skipping",
            )
            return ConfigApplyResult.Skipped
        }

        // 3. Numeric field bounds validation
        val boundsViolations = validateNumericBounds(newConfig)
        if (boundsViolations.isNotEmpty()) {
            Log.e(
                TAG,
                "Config rejected — numeric fields out of bounds: $boundsViolations",
            )
            return ConfigApplyResult.Rejected("INVALID_NUMERIC_BOUNDS")
        }

        // 4. Provisioning-only field immutability check
        val current = _config.value
        if (current != null) {
            val violations = checkProvisioningFields(current, newConfig)
            if (violations.isNotEmpty()) {
                Log.e(
                    TAG,
                    "REPROVISION_REQUIRED: provisioning-only fields changed: $violations",
                )
                return ConfigApplyResult.Rejected("REPROVISION_REQUIRED")
            }

            // 5. Detect restart-required field changes
            val restartFields = detectRestartRequiredChanges(current, newConfig)
            if (restartFields.isNotEmpty()) {
                Log.w(
                    TAG,
                    "Restart-required fields changed: $restartFields. " +
                        "Config stored; service restart needed to apply these changes.",
                )
            }
        }

        // 6. Persist to Room (staged → active atomically for single-row table)
        try {
            val entity = AgentConfig(
                configJson = rawJson,
                configVersion = newConfig.configVersion,
                schemaVersion = majorVersion.toIntOrNull() ?: 2,
                receivedAt = Instant.now().toString(),
            )
            agentConfigDao.upsert(entity)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to persist config to Room", e)
            return ConfigApplyResult.Rejected("PERSISTENCE_FAILURE")
        }

        // 7. Apply in memory (hot-reload takes effect on next scheduler cycle)
        _config.value = newConfig
        Log.i(
            TAG,
            "Config applied: version=${newConfig.configVersion}, configId=${newConfig.configId}",
        )

        return ConfigApplyResult.Applied
    }

    /**
     * Validate that all numeric config fields are within safe operational bounds.
     * Returns list of violation descriptions (empty = all OK).
     */
    private fun validateNumericBounds(cfg: EdgeAgentConfigDto): List<String> {
        val violations = mutableListOf<String>()

        // polling
        if (cfg.polling.pullIntervalSeconds !in 5..3600) {
            violations += "polling.pullIntervalSeconds=${cfg.polling.pullIntervalSeconds} (must be 5..3600)"
        }
        if (cfg.polling.batchSize !in 1..1000) {
            violations += "polling.batchSize=${cfg.polling.batchSize} (must be 1..1000)"
        }

        // sync
        if (cfg.sync.uploadBatchSize !in 1..500) {
            violations += "sync.uploadBatchSize=${cfg.sync.uploadBatchSize} (must be 1..500)"
        }
        if (cfg.sync.syncIntervalSeconds !in 5..3600) {
            violations += "sync.syncIntervalSeconds=${cfg.sync.syncIntervalSeconds} (must be 5..3600)"
        }
        if (cfg.sync.statusPollIntervalSeconds !in 5..3600) {
            violations += "sync.statusPollIntervalSeconds=${cfg.sync.statusPollIntervalSeconds} (must be 5..3600)"
        }
        if (cfg.sync.configPollIntervalSeconds !in 10..86400) {
            violations += "sync.configPollIntervalSeconds=${cfg.sync.configPollIntervalSeconds} (must be 10..86400)"
        }

        // buffer
        if (cfg.buffer.retentionDays !in 1..365) {
            violations += "buffer.retentionDays=${cfg.buffer.retentionDays} (must be 1..365)"
        }
        if (cfg.buffer.maxRecords !in 100..500_000) {
            violations += "buffer.maxRecords=${cfg.buffer.maxRecords} (must be 100..500000)"
        }
        if (cfg.buffer.cleanupIntervalHours !in 1..168) {
            violations += "buffer.cleanupIntervalHours=${cfg.buffer.cleanupIntervalHours} (must be 1..168)"
        }

        // fccConnection
        if (cfg.fccConnection.heartbeatIntervalSeconds !in 5..300) {
            violations += "fccConnection.heartbeatIntervalSeconds=${cfg.fccConnection.heartbeatIntervalSeconds} (must be 5..300)"
        }
        if (cfg.fccConnection.port !in 1..65535) {
            violations += "fccConnection.port=${cfg.fccConnection.port} (must be 1..65535)"
        }

        // telemetry
        if (cfg.telemetry.telemetryIntervalSeconds !in 10..3600) {
            violations += "telemetry.telemetryIntervalSeconds=${cfg.telemetry.telemetryIntervalSeconds} (must be 10..3600)"
        }

        // api
        if (cfg.api.localApiPort !in 1..65535) {
            violations += "api.localApiPort=${cfg.api.localApiPort} (must be 1..65535)"
        }

        return violations
    }

    /**
     * Check provisioning-only fields for changes.
     * Returns list of field names that changed (empty = OK).
     */
    private fun checkProvisioningFields(
        current: EdgeAgentConfigDto,
        incoming: EdgeAgentConfigDto,
    ): List<String> {
        val violations = mutableListOf<String>()
        if (current.agent.deviceId != incoming.agent.deviceId) {
            violations += "agent.deviceId"
        }
        if (current.agent.isPrimaryAgent != incoming.agent.isPrimaryAgent) {
            violations += "agent.isPrimaryAgent"
        }
        if (current.site.siteCode != incoming.site.siteCode) {
            violations += "site.siteCode"
        }
        if (current.site.legalEntityId != incoming.site.legalEntityId) {
            violations += "site.legalEntityId"
        }
        if (current.api.localApiPort != incoming.api.localApiPort) {
            violations += "api.localApiPort"
        }
        return violations
    }

    /**
     * Detect restart-required field changes between current and incoming config.
     * Returns list of changed field paths (empty = all changes are hot-reloadable).
     */
    private fun detectRestartRequiredChanges(
        current: EdgeAgentConfigDto,
        incoming: EdgeAgentConfigDto,
    ): List<String> {
        val changed = mutableListOf<String>()

        // fccConnection restart-required fields
        val cc = current.fccConnection
        val ic = incoming.fccConnection
        if (cc.vendor != ic.vendor) changed += "fccConnection.vendor"
        if (cc.host != ic.host) changed += "fccConnection.host"
        if (cc.port != ic.port) changed += "fccConnection.port"
        if (cc.credentialsRef != ic.credentialsRef) changed += "fccConnection.credentialsRef"
        if (cc.protocolType != ic.protocolType) changed += "fccConnection.protocolType"
        if (cc.transactionMode != ic.transactionMode) changed += "fccConnection.transactionMode"

        // polling restart-required
        if (current.polling.cursorStrategy != incoming.polling.cursorStrategy) {
            changed += "polling.cursorStrategy"
        }

        // sync restart-required
        if (current.sync.cloudBaseUrl != incoming.sync.cloudBaseUrl) {
            changed += "sync.cloudBaseUrl"
        }

        // api restart-required
        if (current.api.enableLanApi != incoming.api.enableLanApi) {
            changed += "api.enableLanApi"
        }
        if (current.api.lanApiKeyRef != incoming.api.lanApiKeyRef) {
            changed += "api.lanApiKeyRef"
        }

        return changed
    }
}

/** Result of attempting to apply a new config snapshot. */
sealed class ConfigApplyResult {
    /** Config applied successfully (hot-reload fields take effect next cycle). */
    data object Applied : ConfigApplyResult()

    /** Config version is not newer than current — no action taken. */
    data object Skipped : ConfigApplyResult()

    /** Config rejected due to validation failure. [reason] is a machine-readable code. */
    data class Rejected(val reason: String) : ConfigApplyResult()
}

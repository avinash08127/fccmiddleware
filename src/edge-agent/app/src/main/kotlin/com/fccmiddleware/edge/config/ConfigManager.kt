package com.fccmiddleware.edge.config

import android.util.Base64
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.buffer.dao.AgentConfigDao
import com.fccmiddleware.edge.buffer.entity.AgentConfig
import com.fccmiddleware.edge.security.KeystoreManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.net.URI
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
    private val keystoreManager: KeystoreManager? = null,
    private val encryptedPrefsManager: com.fccmiddleware.edge.security.EncryptedPrefsManager? = null,
) {

    companion object {
        private const val TAG = "ConfigManager"
        private const val SUPPORTED_MAJOR_VERSION = "1"
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
                AppLogger.i(TAG, "No stored config found — awaiting first cloud config push")
                return
            }
            // M-13: Decrypt config if KeystoreManager is available and data appears encrypted
            val configJsonPlain = decryptConfigJson(stored.configJson)
            if (configJsonPlain == null) {
                AppLogger.e(TAG, "Config integrity check failed — stored config may be tampered. Awaiting fresh config from cloud.")
                return
            }
            val parsed = EdgeAgentConfigJson.decode(configJsonPlain)
            _config.value = parsed
            AppLogger.i(
                TAG,
                "Loaded config from local: version=${parsed.configVersion}, " +
                    "configId=${parsed.configId}",
            )
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to load config from local storage", e)
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
            AppLogger.w(
                TAG,
                "Incompatible schema version: ${newConfig.schemaVersion} " +
                    "(supported major: $SUPPORTED_MAJOR_VERSION)",
            )
            return ConfigApplyResult.Rejected("INCOMPATIBLE_SCHEMA_VERSION")
        }

        // 2. Config version ordering — only apply if strictly greater
        val currentVersion = _config.value?.configVersion
        if (currentVersion != null && newConfig.configVersion <= currentVersion) {
            AppLogger.d(
                TAG,
                "Config version ${newConfig.configVersion} <= current $currentVersion — skipping",
            )
            return ConfigApplyResult.Skipped
        }

        // 3. Numeric field bounds validation
        val boundsViolations = validateNumericBounds(newConfig)
        if (boundsViolations.isNotEmpty()) {
            AppLogger.e(
                TAG,
                "Config rejected — numeric fields out of bounds: $boundsViolations",
            )
            return ConfigApplyResult.Rejected("INVALID_NUMERIC_BOUNDS")
        }

        // 3b. M-16: URL security validation — cloudBaseUrl must use HTTPS
        val urlViolations = validateUrls(newConfig)
        if (urlViolations.isNotEmpty()) {
            AppLogger.e(
                TAG,
                "Config rejected — URL security violations: $urlViolations",
            )
            return ConfigApplyResult.Rejected("INSECURE_URL")
        }

        val runtimeViolations = validateRuntimePrerequisites(newConfig)
        if (runtimeViolations.isNotEmpty()) {
            AppLogger.e(TAG, "Config rejected — runtime prerequisites missing: $runtimeViolations")
            return ConfigApplyResult.Rejected("INCOMPLETE_RUNTIME_CONFIGURATION")
        }

        val fccSupportViolation = validateFccSupport(newConfig)
        if (fccSupportViolation != null) {
            AppLogger.e(TAG, "Config rejected — unsupported FCC configuration: $fccSupportViolation")
            return ConfigApplyResult.Rejected("UNSUPPORTED_FCC_CONFIGURATION")
        }

        // 4. Provisioning-only field immutability check
        val current = _config.value
        if (current != null) {
            val violations = checkProvisioningFields(current, newConfig)
            if (violations.isNotEmpty()) {
                AppLogger.e(
                    TAG,
                    "REPROVISION_REQUIRED: provisioning-only fields changed: $violations",
                )
                return ConfigApplyResult.Rejected("REPROVISION_REQUIRED")
            }

            // 5. Detect restart-required field changes
            val restartFields = detectRestartRequiredChanges(current, newConfig)
            if (restartFields.isNotEmpty()) {
                AppLogger.w(
                    TAG,
                    "Restart-required fields changed: $restartFields. " +
                        "Config stored; service restart needed to apply these changes.",
                )
            }
        }

        // 6. Persist to Room (staged → active atomically for single-row table)
        //    M-13: Encrypt config JSON with AES-256-GCM via Android Keystore for integrity+confidentiality
        try {
            val persistedJson = encryptConfigJson(rawJson)
            val entity = AgentConfig(
                configJson = persistedJson,
                configVersion = newConfig.configVersion,
                schemaVersion = majorVersion.toIntOrNull() ?: 2,
                receivedAt = Instant.now().toString(),
            )
            agentConfigDao.upsert(entity)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to persist config to Room", e)
            return ConfigApplyResult.Rejected("PERSISTENCE_FAILURE")
        }

        // 7. Apply in memory (hot-reload takes effect on next scheduler cycle)
        _config.value = newConfig

        // 8. Persist runtime certificate pins from SiteConfig to EncryptedPrefs.
        //    These will be used on next app restart (OkHttp CertificatePinner is immutable).
        if (encryptedPrefsManager != null && newConfig.sync.certificatePins.isNotEmpty()) {
            val currentPins = encryptedPrefsManager.runtimeCertificatePins
            if (currentPins != newConfig.sync.certificatePins) {
                encryptedPrefsManager.runtimeCertificatePins = newConfig.sync.certificatePins
                AppLogger.i(
                    TAG,
                    "Runtime certificate pins updated from SiteConfig " +
                        "(${newConfig.sync.certificatePins.size} pin(s)). " +
                        "New pins will take effect on next app restart.",
                )
            }
        }

        AppLogger.i(
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

        // fcc
        cfg.fcc.pullIntervalSeconds?.let {
            if (it !in 5..3600) {
                violations += "fcc.pullIntervalSeconds=$it (must be 5..3600)"
            }
        }
        cfg.fcc.catchUpPullIntervalSeconds?.let {
            if (it !in 5..3600) {
                violations += "fcc.catchUpPullIntervalSeconds=$it (must be 5..3600)"
            }
        }
        cfg.fcc.hybridCatchUpIntervalSeconds?.let {
            if (it !in 5..3600) {
                violations += "fcc.hybridCatchUpIntervalSeconds=$it (must be 5..3600)"
            }
        }
        if (cfg.fcc.heartbeatIntervalSeconds !in 5..300) {
            violations += "fcc.heartbeatIntervalSeconds=${cfg.fcc.heartbeatIntervalSeconds} (must be 5..300)"
        }
        if (cfg.fcc.heartbeatTimeoutSeconds !in 5..600) {
            violations += "fcc.heartbeatTimeoutSeconds=${cfg.fcc.heartbeatTimeoutSeconds} (must be 5..600)"
        }
        cfg.fcc.port?.let {
            if (it !in 1..65535) {
                violations += "fcc.port=$it (must be 1..65535)"
            }
        }

        // sync
        if (cfg.sync.uploadBatchSize !in 1..500) {
            violations += "sync.uploadBatchSize=${cfg.sync.uploadBatchSize} (must be 1..500)"
        }
        if (cfg.sync.uploadIntervalSeconds !in 5..3600) {
            violations += "sync.uploadIntervalSeconds=${cfg.sync.uploadIntervalSeconds} (must be 5..3600)"
        }
        if (cfg.sync.syncedStatusPollIntervalSeconds !in 5..3600) {
            violations += "sync.syncedStatusPollIntervalSeconds=${cfg.sync.syncedStatusPollIntervalSeconds} (must be 5..3600)"
        }
        if (cfg.sync.configPollIntervalSeconds !in 10..86400) {
            violations += "sync.configPollIntervalSeconds=${cfg.sync.configPollIntervalSeconds} (must be 10..86400)"
        }
        if (cfg.sync.maxReplayBackoffSeconds !in 1..86400) {
            violations += "sync.maxReplayBackoffSeconds=${cfg.sync.maxReplayBackoffSeconds} (must be 1..86400)"
        }
        if (cfg.sync.initialReplayBackoffSeconds !in 1..3600) {
            violations += "sync.initialReplayBackoffSeconds=${cfg.sync.initialReplayBackoffSeconds} (must be 1..3600)"
        }
        if (cfg.sync.maxRecordsPerUploadWindow !in 1..100_000) {
            violations += "sync.maxRecordsPerUploadWindow=${cfg.sync.maxRecordsPerUploadWindow} (must be 1..100000)"
        }

        // buffer
        if (cfg.buffer.retentionDays !in 1..365) {
            violations += "buffer.retentionDays=${cfg.buffer.retentionDays} (must be 1..365)"
        }
        if (cfg.buffer.stalePendingDays !in 1..90) {
            violations += "buffer.stalePendingDays=${cfg.buffer.stalePendingDays} (must be 1..90)"
        }
        if (cfg.buffer.maxRecords !in 100..500_000) {
            violations += "buffer.maxRecords=${cfg.buffer.maxRecords} (must be 100..500000)"
        }
        if (cfg.buffer.cleanupIntervalHours !in 1..168) {
            violations += "buffer.cleanupIntervalHours=${cfg.buffer.cleanupIntervalHours} (must be 1..168)"
        }

        // telemetry
        if (cfg.telemetry.telemetryIntervalSeconds !in 10..3600) {
            violations += "telemetry.telemetryIntervalSeconds=${cfg.telemetry.telemetryIntervalSeconds} (must be 10..3600)"
        }
        if (cfg.telemetry.metricsWindowSeconds !in 10..86_400) {
            violations += "telemetry.metricsWindowSeconds=${cfg.telemetry.metricsWindowSeconds} (must be 10..86400)"
        }

        // localApi
        if (cfg.localApi.localhostPort !in 1..65535) {
            violations += "localApi.localhostPort=${cfg.localApi.localhostPort} (must be 1..65535)"
        }
        if (cfg.localApi.rateLimitPerMinute !in 1..60_000) {
            violations += "localApi.rateLimitPerMinute=${cfg.localApi.rateLimitPerMinute} (must be 1..60000)"
        }

        return violations
    }

    /**
     * M-16 + M-23: Validate that URL fields use secure schemes and are not
     * SSRF vectors (no localhost, loopback, private IPs, or non-standard ports).
     * Returns list of violation descriptions (empty = all OK).
     */
    private fun validateUrls(cfg: EdgeAgentConfigDto): List<String> {
        val violations = mutableListOf<String>()

        validateExternalUrl(cfg.sync.cloudBaseUrl, "sync.cloudBaseUrl", violations)

        cfg.fiscalization.taxAuthorityEndpoint?.let { endpoint ->
            if (endpoint.isNotBlank()) {
                validateExternalUrl(endpoint, "fiscalization.taxAuthorityEndpoint", violations)
            }
        }

        return violations
    }

    /**
     * M-23: Validates a single URL for HTTPS, SSRF-safe host, and valid port range.
     */
    private fun validateExternalUrl(url: String, fieldName: String, violations: MutableList<String>) {
        if (!url.startsWith("https://")) {
            violations += "$fieldName must use HTTPS (got: ${url.take(8)}…)"
            return // remaining checks need a parseable HTTPS URL
        }

        val parsed = try {
            URI(url)
        } catch (e: Exception) {
            violations += "$fieldName is not a valid URL: ${e.message}"
            return
        }

        val host = parsed.host?.lowercase()
        if (host.isNullOrBlank()) {
            violations += "$fieldName has no host"
            return
        }

        // Block localhost and loopback
        if (host == "localhost" || host.startsWith("127.") || host == "::1" || host == "[::1]") {
            violations += "$fieldName must not point to localhost/loopback"
        }

        // Block RFC-1918 private IP ranges and link-local
        if (isPrivateOrReservedIp(host)) {
            violations += "$fieldName must not point to a private/reserved IP address"
        }

        // Block non-standard ports (only 443 allowed for HTTPS)
        val port = parsed.port
        if (port != -1 && port != 443) {
            violations += "$fieldName must use standard HTTPS port 443 (got: $port)"
        }
    }

    /**
     * M-23: Checks whether a hostname looks like a private, link-local, or reserved IP.
     * Does NOT perform DNS resolution (which could itself be an SSRF vector).
     */
    private fun isPrivateOrReservedIp(host: String): Boolean {
        // IPv4 patterns
        if (host.startsWith("10.")) return true
        if (host.startsWith("172.")) {
            val second = host.removePrefix("172.").substringBefore(".").toIntOrNull()
            if (second != null && second in 16..31) return true
        }
        if (host.startsWith("192.168.")) return true
        if (host.startsWith("169.254.")) return true   // link-local
        if (host.startsWith("0.")) return true           // "this" network
        if (host == "0.0.0.0") return true
        // IPv6 patterns (simplified — full IPv6 SSRF is complex)
        val lower = host.removePrefix("[").removeSuffix("]")
        if (lower.startsWith("fe80:")) return true       // link-local
        if (lower.startsWith("fc") || lower.startsWith("fd")) return true // unique-local
        return false
    }

    private fun validateFccSupport(cfg: EdgeAgentConfigDto): String? {
        if (!cfg.requiresFccRuntime()) {
            return null
        }

        val vendor = try {
            com.fccmiddleware.edge.adapter.common.FccVendor.valueOf(
                requireNotNull(cfg.fcc.vendor).uppercase(),
            )
        } catch (_: IllegalArgumentException) {
            return "fcc.vendor=${cfg.fcc.vendor} is unknown"
        }

        if (!com.fccmiddleware.edge.adapter.common.FccVendorSupportMatrix.isSupported(
                vendor,
                requireNotNull(cfg.fcc.connectionProtocol),
            )
        ) {
            return com.fccmiddleware.edge.adapter.common.FccVendorSupportMatrix.unsupportedMessage(
                vendor,
                requireNotNull(cfg.fcc.connectionProtocol),
            )
        }

        return null
    }

    private fun validateRuntimePrerequisites(cfg: EdgeAgentConfigDto): List<String> {
        val violations = mutableListOf<String>()

        if (cfg.localApi.enableLanApi && cfg.localApi.lanApiKeyRef.isNullOrBlank()) {
            violations += "localApi.lanApiKeyRef is required when localApi.enableLanApi=true"
        }

        if (!cfg.requiresFccRuntime()) {
            return violations
        }

        if (cfg.fcc.vendor.isNullOrBlank()) {
            violations += "fcc.vendor is required when fcc.enabled=true"
        }
        if (cfg.fcc.connectionProtocol.isNullOrBlank()) {
            violations += "fcc.connectionProtocol is required when fcc.enabled=true"
        }
        if (cfg.fcc.hostAddress.isNullOrBlank()) {
            violations += "fcc.hostAddress is required when fcc.enabled=true"
        }
        if (cfg.fcc.port == null) {
            violations += "fcc.port is required when fcc.enabled=true"
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
        if (current.identity.deviceId != incoming.identity.deviceId) {
            violations += "identity.deviceId"
        }
        if (current.identity.isPrimaryAgent != incoming.identity.isPrimaryAgent) {
            violations += "identity.isPrimaryAgent"
        }
        if (current.identity.siteCode != incoming.identity.siteCode) {
            violations += "identity.siteCode"
        }
        if (current.identity.legalEntityId != incoming.identity.legalEntityId) {
            violations += "identity.legalEntityId"
        }
        if (current.localApi.localhostPort != incoming.localApi.localhostPort) {
            violations += "localApi.localhostPort"
        }
        return violations
    }

    // -------------------------------------------------------------------------
    // M-13: Config integrity — encrypt/decrypt via Android Keystore AES-256-GCM
    // -------------------------------------------------------------------------

    /**
     * Encrypt config JSON for storage in Room.
     * Returns Base64-encoded ciphertext when KeystoreManager is available,
     * or raw JSON as fallback (pre-provisioning / test environments).
     */
    private fun encryptConfigJson(rawJson: String): String {
        val ks = keystoreManager ?: return rawJson
        val encrypted = ks.storeSecret(KeystoreManager.ALIAS_CONFIG_INTEGRITY, rawJson)
        if (encrypted == null) {
            AppLogger.w(TAG, "Config encryption failed — persisting raw JSON (integrity not protected)")
            return rawJson
        }
        // Prefix with "ENC:" so loadFromLocal can distinguish encrypted from plaintext
        return "ENC:" + Base64.encodeToString(encrypted, Base64.NO_WRAP)
    }

    /**
     * Decrypt config JSON from Room storage.
     * Returns plaintext JSON, or null if decryption fails (tampered data).
     * Handles both encrypted ("ENC:..." prefix) and legacy plaintext configs.
     */
    private fun decryptConfigJson(storedValue: String): String? {
        if (!storedValue.startsWith("ENC:")) {
            // Legacy plaintext config — accept it but log a warning
            AppLogger.w(TAG, "Stored config is not encrypted — accepting legacy plaintext")
            return storedValue
        }
        val ks = keystoreManager ?: run {
            AppLogger.w(TAG, "Encrypted config found but KeystoreManager not available — cannot decrypt")
            return null
        }
        val encrypted = try {
            Base64.decode(storedValue.removePrefix("ENC:"), Base64.NO_WRAP)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to Base64-decode encrypted config", e)
            return null
        }
        return ks.retrieveSecret(KeystoreManager.ALIAS_CONFIG_INTEGRITY, encrypted)
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

        // fcc restart-required fields
        val cc = current.fcc
        val ic = incoming.fcc
        if (cc.vendor != ic.vendor) changed += "fcc.vendor"
        if (cc.hostAddress != ic.hostAddress) changed += "fcc.hostAddress"
        if (cc.port != ic.port) changed += "fcc.port"
        if (cc.credentialRef != ic.credentialRef) changed += "fcc.credentialRef"
        if (cc.connectionProtocol != ic.connectionProtocol) changed += "fcc.connectionProtocol"
        if (cc.transactionMode != ic.transactionMode) changed += "fcc.transactionMode"

        // sync restart-required
        if (current.sync.cursorStrategy != incoming.sync.cursorStrategy) {
            changed += "sync.cursorStrategy"
        }

        if (current.sync.cloudBaseUrl != incoming.sync.cloudBaseUrl) {
            changed += "sync.cloudBaseUrl"
        }

        // localApi restart-required
        if (current.localApi.enableLanApi != incoming.localApi.enableLanApi) {
            changed += "localApi.enableLanApi"
        }
        if (current.localApi.lanApiKeyRef != incoming.localApi.lanApiKeyRef) {
            changed += "localApi.lanApiKeyRef"
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

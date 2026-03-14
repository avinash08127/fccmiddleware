package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.entity.SyncState
import com.fccmiddleware.edge.config.ConfigApplyResult
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.EdgeAgentConfigDto
import com.fccmiddleware.edge.config.SiteDataManager
import java.time.Instant

sealed class ConfigPollExecutionResult {
    data class Applied(val configVersion: Int) : ConfigPollExecutionResult()
    data class Unchanged(val currentConfigVersion: Int?) : ConfigPollExecutionResult()
    data class Skipped(val configVersion: Int) : ConfigPollExecutionResult()
    data class Rejected(val configVersion: Int, val reason: String) : ConfigPollExecutionResult()
    data class RateLimited(val retryAfterSeconds: Long?) : ConfigPollExecutionResult()
    data object Decommissioned : ConfigPollExecutionResult()
    data class TransportFailure(val message: String) : ConfigPollExecutionResult()
    data class Unavailable(val reason: String) : ConfigPollExecutionResult()
}

/**
 * ConfigPollWorker — polls `GET /api/v1/agent/config` for configuration updates.
 *
 * ## Algorithm
 * 1. Read current config version from [ConfigManager].
 * 2. Call `GET /api/v1/agent/config` with `If-None-Match: {configVersion}`.
 * 3. On 304 Not Modified: no-op — config unchanged.
 * 4. On 200 OK: parse the new config, delegate to [ConfigManager.applyConfig],
 *    update [SyncState.lastConfigPullAt] and [SyncState.lastConfigVersion].
 * 5. On 401: attempt one token refresh via [DeviceTokenProvider] and retry.
 * 6. On 403 DEVICE_DECOMMISSIONED: permanently stop all sync.
 * 7. On transport failure: apply exponential backoff, retry on next cadence tick.
 *
 * ## Triggering
 * Called by [CadenceController] every `configPollTickFrequency` ticks when
 * internet is available (FULLY_ONLINE or FCC_UNREACHABLE).
 *
 * The worker can bootstrap before runtime FCC wiring exists, but cloud auth and
 * config persistence dependencies must be present before polls can proceed.
 */
class ConfigPollWorker(
    private val configManager: ConfigManager? = null,
    private val syncStateDao: SyncStateDao? = null,
    private val cloudApiClient: CloudApiClient? = null,
    private val tokenProvider: DeviceTokenProvider? = null,
    private val siteDataManager: SiteDataManager? = null,
) {

    companion object {
        private const val TAG = "ConfigPollWorker"
        private const val DECOMMISSIONED_ERROR_CODE = "DEVICE_DECOMMISSIONED"
        private const val BASE_BACKOFF_MS = 1_000L
        private const val MAX_BACKOFF_MS = 60_000L
    }

    /**
     * M-08: Circuit breaker for config poll operations.
     */
    internal val circuitBreaker = CircuitBreaker(
        name = "ConfigPoll",
        baseBackoffMs = BASE_BACKOFF_MS,
        maxBackoffMs = MAX_BACKOFF_MS,
    )

    internal val consecutiveFailureCount: Int get() = circuitBreaker.consecutiveFailureCount
    internal val nextRetryAt: Instant get() = circuitBreaker.nextRetryAt

    /**
     * Poll cloud for configuration updates.
     * Called by [CadenceController] on the config poll cadence.
     */
    suspend fun pollConfig(): ConfigPollExecutionResult {
        val cm = configManager ?: run {
            AppLogger.d(TAG, "pollConfig() skipped — configManager not wired")
            return ConfigPollExecutionResult.Unavailable("configManager not wired")
        }
        val client = cloudApiClient ?: run {
            AppLogger.d(TAG, "pollConfig() skipped — cloudApiClient not wired")
            return ConfigPollExecutionResult.Unavailable("cloudApiClient not wired")
        }
        val provider = tokenProvider ?: run {
            AppLogger.d(TAG, "pollConfig() skipped — tokenProvider not wired")
            return ConfigPollExecutionResult.Unavailable("tokenProvider not wired")
        }

        if (provider.isDecommissioned()) {
            AppLogger.w(TAG, "pollConfig() skipped — device decommissioned")
            return ConfigPollExecutionResult.Decommissioned
        }

        // M-08: Circuit breaker check
        if (!circuitBreaker.allowRequest()) {
            val waitMs = circuitBreaker.remainingBackoffMs()
            AppLogger.d(TAG, "pollConfig() skipped — circuit breaker (state=${circuitBreaker.state}, ${waitMs}ms remaining)")
            return ConfigPollExecutionResult.Unavailable(
                "circuit breaker open for ${waitMs}ms",
            )
        }

        val token = provider.getAccessToken() ?: run {
            AppLogger.w(TAG, "pollConfig() skipped — no access token available")
            return ConfigPollExecutionResult.Unavailable("no access token available")
        }

        val currentVersion = cm.currentConfigVersion
        AppLogger.d(TAG, "Polling config (currentVersion=$currentVersion)")

        val result = doPoll(client, provider, currentVersion, token)
        return handlePollResult(cm, result)
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private suspend fun doPoll(
        client: CloudApiClient,
        provider: DeviceTokenProvider,
        currentVersion: Int?,
        token: String,
    ): ConfigPollAttemptResult {
        return when (val result = client.getConfig(currentVersion, token)) {
            is CloudConfigPollResult.Success ->
                ConfigPollAttemptResult.NewConfig(result.rawJson, result.etag)

            is CloudConfigPollResult.NotModified ->
                ConfigPollAttemptResult.Unchanged

            is CloudConfigPollResult.Unauthorized -> {
                AppLogger.i(TAG, "Config poll returned 401 — attempting token refresh")
                val refreshed = provider.refreshAccessToken()
                if (!refreshed) {
                    AppLogger.e(TAG, "Token refresh failed during config poll")
                    return ConfigPollAttemptResult.TransportFailure("401 Unauthorized — token refresh failed")
                }
                val freshToken = provider.getAccessToken()
                if (freshToken == null) {
                    // M-04: Critical state bug — refresh reported success but token store is empty/corrupt
                    AppLogger.e(TAG, "CRITICAL: Token refresh succeeded but getAccessToken() returned null — token store may be corrupt")
                    return ConfigPollAttemptResult.TransportFailure("CRITICAL: token store inconsistency — refresh succeeded but no token available")
                }
                AppLogger.i(TAG, "Token refreshed; retrying config poll")
                when (val retryResult = client.getConfig(currentVersion, freshToken)) {
                    is CloudConfigPollResult.Success ->
                        ConfigPollAttemptResult.NewConfig(retryResult.rawJson, retryResult.etag)
                    is CloudConfigPollResult.NotModified ->
                        ConfigPollAttemptResult.Unchanged
                    is CloudConfigPollResult.Unauthorized ->
                        ConfigPollAttemptResult.TransportFailure("401 Unauthorized after token refresh retry")
                    is CloudConfigPollResult.Forbidden ->
                        resolveForbidden(retryResult.errorCode)
                    is CloudConfigPollResult.RateLimited ->
                        ConfigPollAttemptResult.RateLimited(retryResult.retryAfterSeconds)
                    is CloudConfigPollResult.TransportError ->
                        ConfigPollAttemptResult.TransportFailure(retryResult.message)
                }
            }

            is CloudConfigPollResult.RateLimited ->
                ConfigPollAttemptResult.RateLimited(result.retryAfterSeconds)

            is CloudConfigPollResult.Forbidden -> resolveForbidden(result.errorCode)

            is CloudConfigPollResult.TransportError ->
                ConfigPollAttemptResult.TransportFailure(result.message)
        }
    }

    private fun resolveForbidden(errorCode: String?): ConfigPollAttemptResult =
        if (errorCode == DECOMMISSIONED_ERROR_CODE) {
            ConfigPollAttemptResult.Decommissioned
        } else {
            ConfigPollAttemptResult.TransportFailure("403 Forbidden: $errorCode")
        }

    private suspend fun handlePollResult(
        cm: ConfigManager,
        result: ConfigPollAttemptResult,
    ): ConfigPollExecutionResult {
        when (result) {
            is ConfigPollAttemptResult.NewConfig -> {
                val parsed = try {
                    com.fccmiddleware.edge.config.EdgeAgentConfigJson.decode(result.rawJson)
                } catch (e: Exception) {
                    AppLogger.e(TAG, "Failed to parse config JSON from cloud", e)
                    recordFailure("JSON parse error: ${e.message}")
                    return ConfigPollExecutionResult.TransportFailure(
                        "JSON parse error: ${e.message}",
                    )
                }

                val applyResult = cm.applyConfig(parsed, result.rawJson)
                when (applyResult) {
                    is ConfigApplyResult.Applied -> {
                        AppLogger.i(TAG, "Config version ${parsed.configVersion} applied successfully")
                        try {
                            siteDataManager?.syncFromConfig(parsed)
                        } catch (e: Exception) {
                            AppLogger.e(TAG, "Failed to sync site data after config apply", e)
                        }
                        circuitBreaker.recordSuccess()
                        updateSyncState(parsed.configVersion)
                        return ConfigPollExecutionResult.Applied(parsed.configVersion)
                    }
                    is ConfigApplyResult.Skipped -> {
                        AppLogger.d(TAG, "Config version ${parsed.configVersion} skipped (not newer)")
                        circuitBreaker.recordSuccess()
                        updateLastConfigPullAt()
                        return ConfigPollExecutionResult.Skipped(parsed.configVersion)
                    }
                    is ConfigApplyResult.Rejected -> {
                        AppLogger.w(TAG, "Config version ${parsed.configVersion} rejected: ${applyResult.reason}")
                        recordFailure("Config rejected: ${applyResult.reason}")
                        return ConfigPollExecutionResult.Rejected(
                            configVersion = parsed.configVersion,
                            reason = applyResult.reason,
                        )
                    }
                }
            }

            is ConfigPollAttemptResult.Unchanged -> {
                AppLogger.d(TAG, "Config unchanged (304 Not Modified)")
                circuitBreaker.recordSuccess()
                updateLastConfigPullAt()
                return ConfigPollExecutionResult.Unchanged(cm.currentConfigVersion)
            }

            is ConfigPollAttemptResult.RateLimited -> {
                val retryAfter = result.retryAfterSeconds
                if (retryAfter != null) {
                    circuitBreaker.setBackoffSeconds(retryAfter)
                    AppLogger.w(TAG, "Config poll rate limited (429); backing off for ${retryAfter}s")
                    return ConfigPollExecutionResult.RateLimited(retryAfter)
                } else {
                    recordFailure("429 Too Many Requests (no Retry-After header)")
                    return ConfigPollExecutionResult.RateLimited(null)
                }
            }

            is ConfigPollAttemptResult.Decommissioned -> {
                AppLogger.e(TAG, "DEVICE DECOMMISSIONED during config poll. All cloud sync permanently stopped.")
                tokenProvider?.markDecommissioned()
                return ConfigPollExecutionResult.Decommissioned
            }

            is ConfigPollAttemptResult.TransportFailure -> {
                recordFailure(result.message)
                return ConfigPollExecutionResult.TransportFailure(result.message)
            }
        }
    }

    private suspend fun recordFailure(message: String) {
        val backoffMs = circuitBreaker.recordFailure()
        AppLogger.w(
            TAG,
            "Config poll failed (failure #${circuitBreaker.consecutiveFailureCount}, " +
                "state=${circuitBreaker.state}); next retry after ${backoffMs}ms. Error: $message",
        )
    }

    /** Update both lastConfigPullAt and lastConfigVersion after a successful apply. */
    private suspend fun updateSyncState(configVersion: Int) {
        val dao = syncStateDao ?: return
        val now = Instant.now().toString()
        try {
            val current = dao.get()
            val updated = current?.copy(
                lastConfigPullAt = now,
                lastConfigVersion = configVersion,
                updatedAt = now,
            ) ?: SyncState(
                lastFccCursor = null,
                lastUploadAt = null,
                lastStatusPollAt = null,
                lastConfigPullAt = now,
                lastConfigVersion = configVersion,
                updatedAt = now,
            )
            dao.upsert(updated)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to update SyncState after config apply", e)
        }
    }

    /** Update lastConfigPullAt only (304 or skipped config). */
    private suspend fun updateLastConfigPullAt() {
        val dao = syncStateDao ?: return
        val now = Instant.now().toString()
        try {
            val current = dao.get()
            val updated = current?.copy(lastConfigPullAt = now, updatedAt = now)
                ?: SyncState(
                    lastFccCursor = null,
                    lastUploadAt = null,
                    lastStatusPollAt = null,
                    lastConfigPullAt = now,
                    lastConfigVersion = null,
                    updatedAt = now,
                )
            dao.upsert(updated)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to update lastConfigPullAt in SyncState", e)
        }
    }

    // -------------------------------------------------------------------------
    // Internal sealed result
    // -------------------------------------------------------------------------

    private sealed class ConfigPollAttemptResult {
        data class NewConfig(val rawJson: String, val etag: String?) : ConfigPollAttemptResult()
        data object Unchanged : ConfigPollAttemptResult()
        data object Decommissioned : ConfigPollAttemptResult()
        data class RateLimited(val retryAfterSeconds: Long?) : ConfigPollAttemptResult()
        data class TransportFailure(val message: String) : ConfigPollAttemptResult()
    }
}

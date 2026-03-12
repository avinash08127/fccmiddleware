package com.fccmiddleware.edge.sync

import android.util.Log
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.entity.SyncState
import com.fccmiddleware.edge.config.ConfigApplyResult
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.EdgeAgentConfigDto
import kotlinx.serialization.json.Json
import java.time.Instant

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
 * All constructor parameters are nullable so the worker can be registered in DI
 * before security and config modules are wired. Poll calls are no-ops until all
 * required dependencies are non-null.
 */
class ConfigPollWorker(
    private val configManager: ConfigManager? = null,
    private val syncStateDao: SyncStateDao? = null,
    private val cloudApiClient: CloudApiClient? = null,
    private val tokenProvider: DeviceTokenProvider? = null,
) {

    companion object {
        private const val TAG = "ConfigPollWorker"
        private const val DECOMMISSIONED_ERROR_CODE = "DEVICE_DECOMMISSIONED"
        private const val BASE_BACKOFF_MS = 1_000L
        private const val MAX_BACKOFF_MS = 60_000L
    }

    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
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
    suspend fun pollConfig() {
        val cm = configManager ?: run {
            Log.d(TAG, "pollConfig() skipped — configManager not wired")
            return
        }
        val client = cloudApiClient ?: run {
            Log.d(TAG, "pollConfig() skipped — cloudApiClient not wired")
            return
        }
        val provider = tokenProvider ?: run {
            Log.d(TAG, "pollConfig() skipped — tokenProvider not wired")
            return
        }

        if (provider.isDecommissioned()) {
            Log.w(TAG, "pollConfig() skipped — device decommissioned")
            return
        }

        // M-08: Circuit breaker check
        if (!circuitBreaker.allowRequest()) {
            val waitMs = circuitBreaker.remainingBackoffMs()
            Log.d(TAG, "pollConfig() skipped — circuit breaker (state=${circuitBreaker.state}, ${waitMs}ms remaining)")
            return
        }

        val token = provider.getAccessToken() ?: run {
            Log.w(TAG, "pollConfig() skipped — no access token available")
            return
        }

        val currentVersion = cm.currentConfigVersion
        Log.d(TAG, "Polling config (currentVersion=$currentVersion)")

        val result = doPoll(client, provider, currentVersion, token)
        handlePollResult(cm, result)
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
                Log.i(TAG, "Config poll returned 401 — attempting token refresh")
                val refreshed = provider.refreshAccessToken()
                if (!refreshed) {
                    Log.e(TAG, "Token refresh failed during config poll")
                    return ConfigPollAttemptResult.TransportFailure("401 Unauthorized — token refresh failed")
                }
                val freshToken = provider.getAccessToken()
                if (freshToken == null) {
                    // M-04: Critical state bug — refresh reported success but token store is empty/corrupt
                    Log.e(TAG, "CRITICAL: Token refresh succeeded but getAccessToken() returned null — token store may be corrupt")
                    return ConfigPollAttemptResult.TransportFailure("CRITICAL: token store inconsistency — refresh succeeded but no token available")
                }
                Log.i(TAG, "Token refreshed; retrying config poll")
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
    ) {
        when (result) {
            is ConfigPollAttemptResult.NewConfig -> {
                val parsed = try {
                    json.decodeFromString<EdgeAgentConfigDto>(result.rawJson)
                } catch (e: Exception) {
                    Log.e(TAG, "Failed to parse config JSON from cloud", e)
                    recordFailure("JSON parse error: ${e.message}")
                    return
                }

                val applyResult = cm.applyConfig(parsed, result.rawJson)
                when (applyResult) {
                    is ConfigApplyResult.Applied -> {
                        Log.i(TAG, "Config version ${parsed.configVersion} applied successfully")
                        circuitBreaker.recordSuccess()
                        updateSyncState(parsed.configVersion)
                    }
                    is ConfigApplyResult.Skipped -> {
                        Log.d(TAG, "Config version ${parsed.configVersion} skipped (not newer)")
                        circuitBreaker.recordSuccess()
                        updateLastConfigPullAt()
                    }
                    is ConfigApplyResult.Rejected -> {
                        Log.w(TAG, "Config version ${parsed.configVersion} rejected: ${applyResult.reason}")
                        recordFailure("Config rejected: ${applyResult.reason}")
                    }
                }
            }

            is ConfigPollAttemptResult.Unchanged -> {
                Log.d(TAG, "Config unchanged (304 Not Modified)")
                circuitBreaker.recordSuccess()
                updateLastConfigPullAt()
            }

            is ConfigPollAttemptResult.RateLimited -> {
                val retryAfter = result.retryAfterSeconds
                if (retryAfter != null) {
                    circuitBreaker.setBackoffSeconds(retryAfter)
                    Log.w(TAG, "Config poll rate limited (429); backing off for ${retryAfter}s")
                } else {
                    recordFailure("429 Too Many Requests (no Retry-After header)")
                }
            }

            is ConfigPollAttemptResult.Decommissioned -> {
                Log.e(TAG, "DEVICE DECOMMISSIONED during config poll. All cloud sync permanently stopped.")
                tokenProvider?.markDecommissioned()
            }

            is ConfigPollAttemptResult.TransportFailure -> {
                recordFailure(result.message)
            }
        }
    }

    private suspend fun recordFailure(message: String) {
        val backoffMs = circuitBreaker.recordFailure()
        Log.w(
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
            Log.w(TAG, "Failed to update SyncState after config apply", e)
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
            Log.w(TAG, "Failed to update lastConfigPullAt in SyncState", e)
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

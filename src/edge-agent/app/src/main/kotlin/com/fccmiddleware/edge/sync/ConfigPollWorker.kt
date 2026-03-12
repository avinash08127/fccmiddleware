package com.fccmiddleware.edge.sync

import android.util.Log
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.entity.SyncState
import com.fccmiddleware.edge.config.ConfigApplyResult
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.EdgeAgentConfigDto
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
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

    /** Guards compound read-modify-write of [consecutiveFailureCount] + [nextRetryAt]. */
    private val backoffMutex = Mutex()

    internal var consecutiveFailureCount: Int = 0

    internal var nextRetryAt: Instant = Instant.EPOCH

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

        // Backoff check
        val now = Instant.now()
        val currentNextRetry = backoffMutex.withLock { nextRetryAt }
        if (now.isBefore(currentNextRetry)) {
            val waitMs = currentNextRetry.toEpochMilli() - now.toEpochMilli()
            Log.d(TAG, "pollConfig() skipped — backoff active (${waitMs}ms remaining)")
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
                    Log.e(TAG, "Token refresh succeeded but getAccessToken() returned null")
                    return ConfigPollAttemptResult.TransportFailure("401 Unauthorized — no token after refresh")
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
                    is CloudConfigPollResult.TransportError ->
                        ConfigPollAttemptResult.TransportFailure(retryResult.message)
                }
            }

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
                        backoffMutex.withLock {
                            consecutiveFailureCount = 0
                            nextRetryAt = Instant.EPOCH
                        }
                        updateSyncState(parsed.configVersion)
                    }
                    is ConfigApplyResult.Skipped -> {
                        Log.d(TAG, "Config version ${parsed.configVersion} skipped (not newer)")
                        backoffMutex.withLock {
                            consecutiveFailureCount = 0
                            nextRetryAt = Instant.EPOCH
                        }
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
                backoffMutex.withLock {
                    consecutiveFailureCount = 0
                    nextRetryAt = Instant.EPOCH
                }
                updateLastConfigPullAt()
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
        backoffMutex.withLock {
            consecutiveFailureCount++
            val backoffMs = calculateBackoffMs(consecutiveFailureCount)
            nextRetryAt = Instant.now().plusMillis(backoffMs)
            Log.w(
                TAG,
                "Config poll failed (failure #$consecutiveFailureCount); " +
                    "next retry after ${backoffMs}ms. Error: $message",
            )
        }
    }

    internal fun calculateBackoffMs(failureCount: Int): Long {
        if (failureCount <= 0) return 0L
        val shift = (failureCount - 1).coerceAtMost(30)
        val exponential = BASE_BACKOFF_MS * (1L shl shift)
        return minOf(exponential, MAX_BACKOFF_MS)
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
        data class TransportFailure(val message: String) : ConfigPollAttemptResult()
    }
}

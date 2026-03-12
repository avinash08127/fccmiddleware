package com.fccmiddleware.edge.sync

import android.util.Log
import com.fccmiddleware.edge.buffer.dao.PreAuthDao
import com.fccmiddleware.edge.buffer.entity.PreAuthRecord
import java.time.Instant

/**
 * Runtime configuration for [PreAuthCloudForwardWorker].
 */
data class PreAuthCloudForwardWorkerConfig(
    /** Max records to forward per cycle. */
    val batchSize: Int = 20,

    /** Base exponential backoff delay in milliseconds (1 second). */
    val baseBackoffMs: Long = 1_000L,

    /** Maximum backoff delay in milliseconds (60 seconds). */
    val maxBackoffMs: Long = 60_000L,
)

/**
 * PreAuthCloudForwardWorker — forwards local pre-auth records to the cloud.
 *
 * ## Algorithm
 * 1. Query unsynced pre-auth records via [PreAuthDao.getUnsynced] (oldest first).
 * 2. For each record, POST to `/api/v1/preauth` with device JWT.
 * 3. On HTTP 200/201 (Success) or 409 (Conflict — record already exists on cloud):
 *    call [PreAuthDao.markCloudSynced] to set `is_cloud_synced = 1`.
 * 4. On failure: call [PreAuthDao.recordCloudSyncFailure] to increment
 *    `cloud_sync_attempts` — record stays eligible for retry on next cycle.
 *
 * ## Backoff
 * Consecutive transport failures increase the delay: 1 s, 2 s, 4 s, … max 60 s.
 * A single successful forward resets the backoff.
 *
 * ## JWT lifecycle
 * On HTTP 401 the worker attempts one token refresh via [DeviceTokenProvider.refreshAccessToken]
 * and retries. On 403 DEVICE_DECOMMISSIONED all sync stops permanently.
 *
 * ## Triggering
 * Called by [CadenceController] when connectivity state has internet
 * (FULLY_ONLINE or FCC_UNREACHABLE). Runs under the foreground service scope.
 *
 * All constructor parameters are nullable so the worker can be registered in DI
 * before the security modules are wired. Forward calls are no-ops until all
 * required dependencies are non-null.
 */
class PreAuthCloudForwardWorker(
    private val preAuthDao: PreAuthDao? = null,
    private val cloudApiClient: CloudApiClient? = null,
    private val tokenProvider: DeviceTokenProvider? = null,
    val config: PreAuthCloudForwardWorkerConfig = PreAuthCloudForwardWorkerConfig(),
) {

    companion object {
        private const val TAG = "PreAuthForwardWorker"
        private const val DECOMMISSIONED_ERROR_CODE = "DEVICE_DECOMMISSIONED"
    }

    /**
     * M-08: Circuit breaker for pre-auth forward operations.
     */
    internal val circuitBreaker = CircuitBreaker(
        name = "PreAuthForward",
        baseBackoffMs = config.baseBackoffMs,
        maxBackoffMs = config.maxBackoffMs,
    )

    internal val consecutiveFailureCount: Int get() = circuitBreaker.consecutiveFailureCount
    internal val nextRetryAt: Instant get() = circuitBreaker.nextRetryAt

    // -------------------------------------------------------------------------
    // Public API — called by CadenceController
    // -------------------------------------------------------------------------

    /**
     * Forward unsynced pre-auth records to cloud in chronological order.
     *
     * Each record is forwarded individually (not batched) because the cloud
     * endpoint accepts a single pre-auth per call. On any transport failure,
     * the worker stops processing the remaining batch and applies backoff.
     */
    suspend fun forwardUnsyncedPreAuths() {
        val dao = preAuthDao ?: run {
            Log.d(TAG, "forwardUnsyncedPreAuths() skipped — preAuthDao not wired")
            return
        }
        val client = cloudApiClient ?: run {
            Log.d(TAG, "forwardUnsyncedPreAuths() skipped — cloudApiClient not wired")
            return
        }
        val provider = tokenProvider ?: run {
            Log.d(TAG, "forwardUnsyncedPreAuths() skipped — tokenProvider not wired")
            return
        }

        if (provider.isDecommissioned()) {
            Log.w(TAG, "forwardUnsyncedPreAuths() skipped — device decommissioned")
            return
        }

        // M-08: Circuit breaker check
        if (!circuitBreaker.allowRequest()) {
            val waitMs = circuitBreaker.remainingBackoffMs()
            Log.d(TAG, "forwardUnsyncedPreAuths() skipped — circuit breaker (state=${circuitBreaker.state}, ${waitMs}ms remaining)")
            return
        }

        val unsynced = dao.getUnsynced(config.batchSize)
        if (unsynced.isEmpty()) {
            // H-02 fix: reset when no unsynced records so new records forward immediately
            circuitBreaker.recordSuccess()
            Log.d(TAG, "forwardUnsyncedPreAuths() — no unsynced pre-auth records, circuit breaker reset")
            return
        }

        Log.i(TAG, "Forwarding ${unsynced.size} unsynced pre-auth records to cloud")

        val token = provider.getAccessToken() ?: run {
            Log.w(TAG, "forwardUnsyncedPreAuths() skipped — no access token available")
            return
        }

        for (record in unsynced) {
            if (record.unitPrice == null) {
                val attemptNow = Instant.now().toString()
                dao.recordCloudSyncFailure(record.id, attemptNow)
                Log.w(
                    TAG,
                    "Skipping pre-auth ${record.odooOrderId} cloud forward: unitPrice is missing " +
                        "for a legacy local record.",
                )
                continue
            }

            val result = doForward(client, provider, record, token)
            when (result) {
                is ForwardAttemptResult.Success -> {
                    val attemptNow = Instant.now().toString()
                    dao.markCloudSynced(record.id, attemptNow)
                    circuitBreaker.recordSuccess()
                    Log.i(TAG, "Pre-auth ${record.odooOrderId} forwarded to cloud")
                }

                is ForwardAttemptResult.RateLimited -> {
                    val retryAfter = result.retryAfterSeconds
                    if (retryAfter != null) {
                        circuitBreaker.setBackoffSeconds(retryAfter)
                        Log.w(TAG, "Pre-auth forward rate limited (429); backing off for ${retryAfter}s")
                    } else {
                        val backoffMs = circuitBreaker.recordFailure()
                        Log.w(TAG, "Pre-auth forward rate limited (429); no Retry-After, using backoff ${backoffMs}ms")
                    }
                    return
                }

                is ForwardAttemptResult.Decommissioned -> {
                    Log.e(TAG, "DEVICE DECOMMISSIONED during pre-auth forward. All sync stopped.")
                    provider.markDecommissioned()
                    return
                }

                is ForwardAttemptResult.TransportFailure -> {
                    val attemptNow = Instant.now().toString()
                    dao.recordCloudSyncFailure(record.id, attemptNow)
                    val backoffMs = circuitBreaker.recordFailure()
                    Log.w(
                        TAG,
                        "Pre-auth forward failed (failure #${circuitBreaker.consecutiveFailureCount}, " +
                            "state=${circuitBreaker.state}); next retry after ${backoffMs}ms. " +
                            "Error: ${result.message}",
                    )
                    // Stop processing remaining records — retry on next cycle
                    return
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private suspend fun doForward(
        client: CloudApiClient,
        provider: DeviceTokenProvider,
        record: PreAuthRecord,
        token: String,
    ): ForwardAttemptResult {
        val request = record.toForwardRequest()
        return when (val result = client.forwardPreAuth(request, token)) {
            is CloudPreAuthForwardResult.Success ->
                ForwardAttemptResult.Success

            is CloudPreAuthForwardResult.Conflict ->
                // 409 means cloud already has this record — treat as synced
                ForwardAttemptResult.Success

            is CloudPreAuthForwardResult.Unauthorized -> {
                Log.i(TAG, "Pre-auth forward returned 401 — attempting token refresh")
                val refreshed = provider.refreshAccessToken()
                if (!refreshed) {
                    Log.e(TAG, "Token refresh failed during pre-auth forward")
                    return ForwardAttemptResult.TransportFailure(
                        "401 Unauthorized — token refresh failed",
                    )
                }
                val freshToken = provider.getAccessToken()
                if (freshToken == null) {
                    Log.e(TAG, "Token refresh succeeded but getAccessToken() returned null")
                    return ForwardAttemptResult.TransportFailure(
                        "401 Unauthorized — no token after refresh",
                    )
                }
                Log.i(TAG, "Token refreshed; retrying pre-auth forward")
                when (val retryResult = client.forwardPreAuth(request, freshToken)) {
                    is CloudPreAuthForwardResult.Success ->
                        ForwardAttemptResult.Success

                    is CloudPreAuthForwardResult.Conflict ->
                        ForwardAttemptResult.Success

                    is CloudPreAuthForwardResult.Unauthorized ->
                        ForwardAttemptResult.TransportFailure(
                            "401 Unauthorized after token refresh retry",
                        )

                    is CloudPreAuthForwardResult.RateLimited ->
                        ForwardAttemptResult.RateLimited(retryResult.retryAfterSeconds)

                    is CloudPreAuthForwardResult.Forbidden ->
                        resolveForbidden(retryResult.errorCode)

                    is CloudPreAuthForwardResult.TransportError ->
                        ForwardAttemptResult.TransportFailure(retryResult.message)
                }
            }

            is CloudPreAuthForwardResult.RateLimited ->
                ForwardAttemptResult.RateLimited(result.retryAfterSeconds)

            is CloudPreAuthForwardResult.Forbidden ->
                resolveForbidden(result.errorCode)

            is CloudPreAuthForwardResult.TransportError ->
                ForwardAttemptResult.TransportFailure(result.message)
        }
    }

    private fun resolveForbidden(errorCode: String?): ForwardAttemptResult =
        if (errorCode == DECOMMISSIONED_ERROR_CODE) {
            ForwardAttemptResult.Decommissioned
        } else {
            ForwardAttemptResult.TransportFailure("403 Forbidden: $errorCode")
        }

    private fun PreAuthRecord.toForwardRequest(): PreAuthForwardRequest =
        PreAuthForwardRequest(
            siteCode = siteCode,
            odooOrderId = odooOrderId,
            pumpNumber = pumpNumber,
            nozzleNumber = nozzleNumber,
            productCode = productCode,
            requestedAmount = requestedAmountMinorUnits,
            unitPrice = requireNotNull(unitPrice),
            currency = currencyCode,
            status = status,
            requestedAt = requestedAt,
            expiresAt = expiresAt,
            fccCorrelationId = fccCorrelationId,
            fccAuthorizationCode = fccAuthorizationCode,
            customerName = customerName,
            customerTaxId = customerTaxId,
        )

    // -------------------------------------------------------------------------
    // Internal sealed result
    // -------------------------------------------------------------------------

    private sealed class ForwardAttemptResult {
        data object Success : ForwardAttemptResult()
        data object Decommissioned : ForwardAttemptResult()
        data class RateLimited(val retryAfterSeconds: Long?) : ForwardAttemptResult()
        data class TransportFailure(val message: String) : ForwardAttemptResult()
    }
}

package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.buffer.dao.PreAuthDao
import com.fccmiddleware.edge.buffer.entity.PreAuthRecord
import com.fccmiddleware.edge.security.KeystoreBackedStringCipher
import com.fccmiddleware.edge.security.KeystoreManager
import java.time.Instant
import java.util.Locale
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit

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

    /**
     * PA-P04: Maximum number of concurrent HTTP forward calls within one cycle.
     * Bounded parallelism reduces drain time for large backlogs without
     * overwhelming the cloud endpoint. Default 3.
     */
    val maxConcurrency: Int = 3,
)

/**
 * PreAuthCloudForwardWorker — forwards local pre-auth records to the cloud.
 *
 * ## Algorithm
 * 1. Query unsynced pre-auth records via [PreAuthDao.getUnsynced] (oldest first).
 * 2. For each record, POST to `/api/v1/preauth` with device JWT.
 * 3. On HTTP 200/201, or on 409 `CONFLICT.INVALID_TRANSITION` where the cloud is already
 *    ahead of the edge state, call [PreAuthDao.markCloudSynced] to set `is_cloud_synced = 1`.
 *    Retryable 409 race conditions are treated as failures so the record is retried.
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
    private val keystoreManager: KeystoreManager? = null,
    private val configManager: ConfigManager? = null,
    val config: PreAuthCloudForwardWorkerConfig = PreAuthCloudForwardWorkerConfig(),
) {

    /**
     * P2-08: Callback invoked when a cloud response indicates the peer directory is stale.
     */
    var onPeerDirectoryStale: (() -> Unit)? = null

    companion object {
        private const val TAG = "PreAuthForwardWorker"
        private const val DECOMMISSIONED_ERROR_CODE = "DEVICE_DECOMMISSIONED"
        private const val INVALID_TRANSITION_CONFLICT_CODE = "INVALID_TRANSITION"
        private const val RACE_CONDITION_CONFLICT_CODE = "RACE_CONDITION"
    }

    /**
     * M-08: Circuit breaker for pre-auth forward operations.
     */
    internal val circuitBreaker = CircuitBreaker(
        name = "PreAuthForward",
        baseBackoffMs = config.baseBackoffMs,
        maxBackoffMs = config.maxBackoffMs,
    )
    private val customerTaxIdCipher = KeystoreBackedStringCipher(
        keystoreManager = keystoreManager,
        alias = KeystoreManager.ALIAS_PREAUTH_PII,
    )

    internal val consecutiveFailureCount: Int get() = circuitBreaker.consecutiveFailureCount
    internal val nextRetryAt: Instant get() = circuitBreaker.nextRetryAt

    // -------------------------------------------------------------------------
    // Public API — called by CadenceController
    // -------------------------------------------------------------------------

    /**
     * Forward unsynced pre-auth records to cloud with bounded concurrency.
     *
     * PA-P04: Records are forwarded in parallel (up to [PreAuthCloudForwardWorkerConfig.maxConcurrency]
     * concurrent HTTP calls) to reduce drain time for large backlogs after connectivity restoration.
     * Each record is still forwarded individually because the cloud endpoint accepts a single
     * pre-auth per call. On any transport failure all remaining unstarted work is skipped and
     * backoff is applied.
     */
    suspend fun forwardUnsyncedPreAuths() {
        val dao = preAuthDao ?: run {
            AppLogger.d(TAG, "forwardUnsyncedPreAuths() skipped — preAuthDao not wired")
            return
        }
        val client = cloudApiClient ?: run {
            AppLogger.d(TAG, "forwardUnsyncedPreAuths() skipped — cloudApiClient not wired")
            return
        }
        val provider = tokenProvider ?: run {
            AppLogger.d(TAG, "forwardUnsyncedPreAuths() skipped — tokenProvider not wired")
            return
        }

        if (provider.isDecommissioned()) {
            AppLogger.w(TAG, "forwardUnsyncedPreAuths() skipped — device decommissioned")
            return
        }

        // M-08: Circuit breaker check
        if (!circuitBreaker.allowRequest()) {
            val waitMs = circuitBreaker.remainingBackoffMs()
            AppLogger.d(TAG, "forwardUnsyncedPreAuths() skipped — circuit breaker (state=${circuitBreaker.state}, ${waitMs}ms remaining)")
            return
        }

        val unsynced = dao.getUnsynced(config.batchSize)
        if (unsynced.isEmpty()) {
            // H-02 fix: reset when no unsynced records so new records forward immediately
            circuitBreaker.recordSuccess()
            AppLogger.d(TAG, "forwardUnsyncedPreAuths() — no unsynced pre-auth records, circuit breaker reset")
            return
        }

        AppLogger.i(TAG, "Forwarding ${unsynced.size} unsynced pre-auth records to cloud")

        val token = provider.getAccessToken() ?: run {
            AppLogger.w(TAG, "forwardUnsyncedPreAuths() skipped — no access token available")
            return
        }

        val semaphore = Semaphore(config.maxConcurrency)
        // PA-P04: shared stop flag — set by the first failure to skip remaining unstarted records.
        val shouldStop = AtomicBoolean(false)

        coroutineScope {
            for (record in unsynced) {
                if (shouldStop.get()) break

                if (record.unitPrice == null) {
                    // M-20: Legacy records with null unitPrice can never be forwarded.
                    // Mark as cloud-synced so they don't stay in the unsynced queue forever.
                    val attemptNow = Instant.now().toString()
                    dao.markCloudSynced(record.id, attemptNow)
                    AppLogger.w(
                        TAG,
                        "Pre-auth ${record.odooOrderId} marked as synced (incomplete): unitPrice is missing " +
                            "for a legacy local record — record will not be forwarded to cloud.",
                    )
                    continue
                }

                launch {
                    semaphore.withPermit {
                        if (shouldStop.get()) return@withPermit

                        val result = doForward(client, provider, record, token)
                        when (result) {
                            is ForwardAttemptResult.Success -> {
                                val attemptNow = Instant.now().toString()
                                dao.markCloudSynced(record.id, attemptNow)
                                circuitBreaker.recordSuccess()
                                AppLogger.i(TAG, "Pre-auth ${record.odooOrderId} forwarded to cloud")
                            }

                            is ForwardAttemptResult.RateLimited -> {
                                val retryAfter = result.retryAfterSeconds
                                if (retryAfter != null) {
                                    circuitBreaker.setBackoffSeconds(retryAfter)
                                    AppLogger.w(TAG, "Pre-auth forward rate limited (429); backing off for ${retryAfter}s")
                                } else {
                                    val backoffMs = circuitBreaker.recordFailure()
                                    AppLogger.w(TAG, "Pre-auth forward rate limited (429); no Retry-After, using backoff ${backoffMs}ms")
                                }
                                shouldStop.set(true)
                            }

                            is ForwardAttemptResult.Decommissioned -> {
                                AppLogger.e(TAG, "DEVICE DECOMMISSIONED during pre-auth forward. All sync stopped.")
                                provider.markDecommissioned()
                                shouldStop.set(true)
                            }

                            is ForwardAttemptResult.TransportFailure -> {
                                val attemptNow = Instant.now().toString()
                                dao.recordCloudSyncFailure(record.id, attemptNow)
                                val backoffMs = circuitBreaker.recordFailure()
                                AppLogger.w(
                                    TAG,
                                    "Pre-auth forward failed (failure #${circuitBreaker.consecutiveFailureCount}, " +
                                        "state=${circuitBreaker.state}); next retry after ${backoffMs}ms. " +
                                        "Error: ${result.message}",
                                )
                                shouldStop.set(true)
                            }
                        }
                    }
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
        val request = try {
            buildForwardRequest(record)
        } catch (e: CancellationException) {
            throw e
        } catch (e: IllegalStateException) {
            return ForwardAttemptResult.TransportFailure(
                "Failed to prepare pre-auth payload for cloud forward: ${e.message}",
            )
        } catch (e: Exception) {
            return ForwardAttemptResult.TransportFailure(
                "Failed to prepare pre-auth payload for cloud forward: ${e::class.simpleName}: ${e.message}",
            )
        }
        return when (val result = client.forwardPreAuth(request, token)) {
            is CloudPreAuthForwardResult.Success -> {
                checkPeerDirectoryVersion(result.peerDirectoryVersion)
                ForwardAttemptResult.Success
            }

            is CloudPreAuthForwardResult.Conflict ->
                resolveConflict(record, result)

            is CloudPreAuthForwardResult.Unauthorized -> {
                AppLogger.i(TAG, "Pre-auth forward returned 401 — attempting token refresh")
                val refreshed = provider.refreshAccessToken()
                if (!refreshed) {
                    AppLogger.e(TAG, "Token refresh failed during pre-auth forward")
                    return ForwardAttemptResult.TransportFailure(
                        "401 Unauthorized — token refresh failed",
                    )
                }
                val freshToken = provider.getAccessToken()
                if (freshToken == null) {
                    AppLogger.e(TAG, "Token refresh succeeded but getAccessToken() returned null")
                    return ForwardAttemptResult.TransportFailure(
                        "401 Unauthorized — no token after refresh",
                    )
                }
                AppLogger.i(TAG, "Token refreshed; retrying pre-auth forward")
                when (val retryResult = client.forwardPreAuth(request, freshToken)) {
                    is CloudPreAuthForwardResult.Success -> {
                        checkPeerDirectoryVersion(retryResult.peerDirectoryVersion)
                        ForwardAttemptResult.Success
                    }

                    is CloudPreAuthForwardResult.Conflict ->
                        resolveConflict(record, retryResult)

                    is CloudPreAuthForwardResult.Unauthorized ->
                        ForwardAttemptResult.TransportFailure(
                            "401 Unauthorized after token refresh retry",
                        )

                    is CloudPreAuthForwardResult.RateLimited ->
                        ForwardAttemptResult.RateLimited(retryResult.retryAfterSeconds)

                    is CloudPreAuthForwardResult.Forbidden ->
                        resolveForbidden(retryResult.errorCode)

                    is CloudPreAuthForwardResult.BadRequest -> {
                        AppLogger.w(TAG, "Pre-auth forward 400 after retry: ${retryResult.errorCode}")
                        ForwardAttemptResult.TransportFailure("400 Bad Request: ${retryResult.errorCode}")
                    }

                    is CloudPreAuthForwardResult.TransportError ->
                        ForwardAttemptResult.TransportFailure(retryResult.message)
                }
            }

            is CloudPreAuthForwardResult.RateLimited ->
                ForwardAttemptResult.RateLimited(result.retryAfterSeconds)

            is CloudPreAuthForwardResult.Forbidden ->
                resolveForbidden(result.errorCode)

            is CloudPreAuthForwardResult.BadRequest -> {
                AppLogger.w(TAG, "Pre-auth forward 400: ${result.errorCode}")
                ForwardAttemptResult.TransportFailure("400 Bad Request: ${result.errorCode}")
            }

            is CloudPreAuthForwardResult.TransportError ->
                ForwardAttemptResult.TransportFailure(result.message)
        }
    }

    /** P2-08: Check if cloud's peer directory version indicates staleness and trigger config refresh. */
    private fun checkPeerDirectoryVersion(cloudVersion: Long?) {
        val cm = configManager ?: return
        if (cloudVersion == null) return
        if (cm.isPeerDirectoryStale(cloudVersion)) {
            AppLogger.i(TAG, "Peer directory stale: cloud=$cloudVersion > local=${cm.currentPeerDirectoryVersion}")
            cm.updatePeerDirectoryVersion(cloudVersion)
            onPeerDirectoryStale?.invoke()
        } else {
            cm.updatePeerDirectoryVersion(cloudVersion)
        }
    }

    private fun resolveConflict(
        record: PreAuthRecord,
        result: CloudPreAuthForwardResult.Conflict,
    ): ForwardAttemptResult =
        when (normalizeConflictCode(result.errorCode)) {
            INVALID_TRANSITION_CONFLICT_CODE -> {
                AppLogger.w(
                    TAG,
                    "Pre-auth ${record.odooOrderId} returned 409 ${result.errorCode}; " +
                        "treating as synced because the cloud record is already in a more advanced state.",
                )
                ForwardAttemptResult.Success
            }

            RACE_CONDITION_CONFLICT_CODE ->
                ForwardAttemptResult.TransportFailure(
                    "409 Conflict (retryable race condition): ${result.message ?: result.errorCode ?: "unknown"}",
                )

            else ->
                ForwardAttemptResult.TransportFailure(
                    "409 Conflict: ${listOfNotNull(result.errorCode, result.message).joinToString(" - ").ifBlank { "unknown conflict" }}",
                )
        }

    private fun normalizeConflictCode(errorCode: String?): String? =
        errorCode
            ?.substringAfterLast('.')
            ?.uppercase(Locale.ROOT)

    private fun resolveForbidden(errorCode: String?): ForwardAttemptResult =
        if (errorCode == DECOMMISSIONED_ERROR_CODE) {
            ForwardAttemptResult.Decommissioned
        } else {
            ForwardAttemptResult.TransportFailure("403 Forbidden: $errorCode")
        }

    private suspend fun buildForwardRequest(record: PreAuthRecord): PreAuthForwardRequest {
        val customerTaxId = resolveCustomerTaxIdForForwarding(record)
        val leaderEpoch = configManager?.config?.value?.siteHa?.leaderEpoch?.takeIf { it > 0 }
        return record.toForwardRequest(customerTaxId, leaderEpoch)
    }

    private suspend fun resolveCustomerTaxIdForForwarding(record: PreAuthRecord): String? {
        val storedValue = record.customerTaxId ?: return null
        if (storedValue.isBlank()) return storedValue

        return if (customerTaxIdCipher.isEncrypted(storedValue)) {
            customerTaxIdCipher.decryptFromStorage(storedValue)
        } else {
            val encryptedValue = customerTaxIdCipher.encryptForStorage(storedValue)
            requireNotNull(preAuthDao) { "preAuthDao is required for legacy customerTaxId migration" }
                .updateCustomerTaxId(record.id, encryptedValue)
            storedValue
        }
    }

    private fun PreAuthRecord.toForwardRequest(customerTaxId: String?, leaderEpoch: Long?): PreAuthForwardRequest =
        PreAuthForwardRequest(
            siteCode = siteCode,
            odooOrderId = odooOrderId,
            pumpNumber = pumpNumber,
            nozzleNumber = nozzleNumber,
            productCode = productCode,
            requestedAmount = requestedAmountMinorUnits,
            unitPrice = requireNotNull(unitPrice),
            currency = currencyCode,
            status = status.name,
            requestedAt = requestedAt,
            expiresAt = expiresAt,
            leaderEpoch = leaderEpoch,
            fccCorrelationId = fccCorrelationId,
            fccAuthorizationCode = fccAuthorizationCode,
            // NET-008: Map vehicleNumber and customerBusinessName for cloud reconciliation.
            vehicleNumber = vehicleNumber,
            customerName = customerName,
            customerTaxId = customerTaxId,
            customerBusinessName = customerBusinessName,
            attendantId = attendantId,
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

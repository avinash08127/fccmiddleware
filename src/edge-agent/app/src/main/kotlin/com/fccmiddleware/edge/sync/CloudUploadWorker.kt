package com.fccmiddleware.edge.sync

import android.util.Log
import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.buffer.entity.SyncState
import java.time.Instant

/**
 * Runtime configuration for [CloudUploadWorker].
 *
 * Sourced from SiteConfig when [ConfigManager] is wired (EA-2.x).
 * Defaults match the spec values.
 */
data class CloudUploadWorkerConfig(
    /** Number of records per upload batch. Default 50; cloud max is 500. */
    val uploadBatchSize: Int = 50,

    /** Base exponential backoff delay in milliseconds (1 second). */
    val baseBackoffMs: Long = 1_000L,

    /** Maximum backoff delay in milliseconds (60 seconds). */
    val maxBackoffMs: Long = 60_000L,
)

/**
 * CloudUploadWorker — uploads buffered transactions to the cloud backend.
 *
 * ## Upload algorithm
 * 1. Query PENDING records ordered by `createdAt ASC` (oldest first).
 * 2. Batch into groups of [CloudUploadWorkerConfig.uploadBatchSize].
 * 3. POST to `/api/v1/transactions/upload` with device JWT.
 * 4. Process per-record outcomes:
 *    - `ACCEPTED`  → mark `syncStatus = UPLOADED`
 *    - `DUPLICATE` → mark `syncStatus = UPLOADED` (never retry; cloud dedup confirmed)
 *    - `REJECTED`  → increment `uploadAttempts`, log; record stays PENDING for next cycle
 * 5. On HTTP failure: record failure, apply exponential backoff, retry on next cadence tick.
 *
 * ## Ordering guarantee
 * The oldest PENDING record is always the head of the next batch. A transport failure
 * on record N does NOT cause the worker to skip to N+1 on the next call — the same
 * PENDING batch is retried after the backoff expires.
 *
 * ## Backoff
 * Consecutive failures increase the delay: 1 s, 2 s, 4 s, 8 s, … max 60 s.
 * Tracked in-memory via [consecutiveFailureCount] and [nextRetryAt].
 * A single successful batch resets the backoff.
 *
 * ## JWT lifecycle
 * On HTTP 401 the worker attempts one token refresh via [DeviceTokenProvider.refreshAccessToken]
 * and retries the same batch once. On 403 DEVICE_DECOMMISSIONED all sync stops permanently.
 *
 * ## Triggering
 * Called by [CadenceController] when connectivity state is FULLY_ONLINE or FCC_UNREACHABLE.
 * Never uses WorkManager — runs under the foreground service scope.
 *
 * All constructor parameters are nullable so the worker can be registered in DI before
 * the security and config modules are wired (EA-2.x). Upload calls are no-ops until all
 * required dependencies are non-null.
 */
class CloudUploadWorker(
    private val bufferManager: TransactionBufferManager? = null,
    private val syncStateDao: SyncStateDao? = null,
    private val cloudApiClient: CloudApiClient? = null,
    private val tokenProvider: DeviceTokenProvider? = null,
    val config: CloudUploadWorkerConfig = CloudUploadWorkerConfig(),
    private val telemetryReporter: TelemetryReporter? = null,
) {

    companion object {
        private const val TAG = "CloudUploadWorker"
        private const val DECOMMISSIONED_ERROR_CODE = "DEVICE_DECOMMISSIONED"
        private const val SYNCED_TO_ODOO_STATUS = "SYNCED_TO_ODOO"
        private const val STATUS_NOT_FOUND = "NOT_FOUND"
        private const val STATUS_STALE_PENDING = "STALE_PENDING"
        private const val STATUS_DUPLICATE = "DUPLICATE"

        /** Cloud API rejects batches larger than this. */
        private const val CLOUD_MAX_BATCH_SIZE = 500
    }

    /**
     * M-15: Effective batch size — starts at config value, halved on 413 PayloadTooLarge,
     * reset to config value on successful upload. Floor: 1 record.
     */
    @Volatile
    internal var effectiveBatchSize: Int = config.uploadBatchSize

    /**
     * M-08: Circuit breaker for upload operations.
     * Replaces the manual backoff mutex/counters with a circuit breaker that
     * enters OPEN state after [CircuitBreaker.openThreshold] consecutive failures,
     * blocking further attempts until connectivity recovery or half-open probe.
     */
    internal val uploadCircuitBreaker = CircuitBreaker(
        name = "CloudUpload",
        baseBackoffMs = config.baseBackoffMs,
        maxBackoffMs = config.maxBackoffMs,
    )

    // Convenience aliases for test compatibility
    internal val consecutiveFailureCount: Int get() = uploadCircuitBreaker.consecutiveFailureCount
    internal val nextRetryAt: Instant get() = uploadCircuitBreaker.nextRetryAt

    // -------------------------------------------------------------------------
    // Public API — called by CadenceController
    // -------------------------------------------------------------------------

    /**
     * Upload PENDING buffered transactions to cloud in chronological order.
     *
     * Never skips past a failed record (replay ordering guarantee).
     * See class-level KDoc for the full algorithm.
     */
    suspend fun uploadPendingBatch() {
        val bm = bufferManager ?: run {
            Log.d(TAG, "uploadPendingBatch() skipped — bufferManager not wired")
            return
        }
        val client = cloudApiClient ?: run {
            Log.d(TAG, "uploadPendingBatch() skipped — cloudApiClient not wired")
            return
        }
        val provider = tokenProvider ?: run {
            Log.d(TAG, "uploadPendingBatch() skipped — tokenProvider not wired")
            return
        }

        if (provider.isDecommissioned()) {
            Log.w(TAG, "uploadPendingBatch() skipped — device decommissioned; no further sync")
            return
        }

        // M-08: Circuit breaker check — skip if backoff active or circuit is OPEN
        if (!uploadCircuitBreaker.allowRequest()) {
            val waitMs = uploadCircuitBreaker.remainingBackoffMs()
            Log.d(TAG, "uploadPendingBatch() skipped — circuit breaker (state=${uploadCircuitBreaker.state}, ${waitMs}ms remaining)")
            return
        }

        val batchSize = effectiveBatchSize.coerceAtMost(CLOUD_MAX_BATCH_SIZE)
        val batch = bm.getPendingBatch(batchSize)
        if (batch.isEmpty()) {
            // H-02 fix: reset backoff when buffer drains so new records upload immediately
            uploadCircuitBreaker.recordSuccess()
            Log.d(TAG, "uploadPendingBatch() — no PENDING records, circuit breaker reset")
            return
        }

        Log.i(TAG, "Starting upload batch: ${batch.size} records")

        val token = provider.getAccessToken() ?: run {
            // H-05: Re-check decommission status — if device was decommissioned between
            // the initial check and getAccessToken(), a null token is expected and permanent.
            if (provider.isDecommissioned()) {
                Log.w(TAG, "uploadPendingBatch() skipped — device decommissioned (detected on token fetch)")
            } else {
                Log.w(TAG, "uploadPendingBatch() skipped — no access token available (not provisioned)")
            }
            return
        }

        val result = doUpload(client, provider, batch, token)
        handleUploadResult(bm, batch, result)
    }

    /**
     * M-08: Circuit breaker for status poll operations.
     */
    internal val statusPollCircuitBreaker = CircuitBreaker(
        name = "StatusPoll",
        baseBackoffMs = config.baseBackoffMs,
        maxBackoffMs = config.maxBackoffMs,
    )

    // Convenience aliases for test compatibility
    internal val statusPollConsecutiveFailureCount: Int get() = statusPollCircuitBreaker.consecutiveFailureCount
    internal val statusPollNextRetryAt: Instant get() = statusPollCircuitBreaker.nextRetryAt

    /**
     * Poll cloud for transactions confirmed SYNCED_TO_ODOO.
     * Shares the cadence loop with cloud health checks (per spec §5.4).
     *
     * ## Algorithm
     * 1. Query local buffer for UPLOADED records' fccTransactionIds (max 500).
     * 2. Call `GET /api/v1/transactions/synced-status?ids=...` with device JWT.
     * 3. For each entry with status SYNCED_TO_ODOO: call `bufferManager.markSyncedToOdoo()`.
     * 4. Update `SyncState.lastStatusPollAt`.
     *
     * On HTTP 401: attempt one token refresh and retry.
     * On 403 DEVICE_DECOMMISSIONED: stop all sync permanently.
     * On transport failure: apply exponential backoff, retry on next cadence tick.
     */
    suspend fun pollSyncedToOdooStatus() {
        val bm = bufferManager ?: run {
            Log.d(TAG, "pollSyncedToOdooStatus() skipped — bufferManager not wired")
            return
        }
        val client = cloudApiClient ?: run {
            Log.d(TAG, "pollSyncedToOdooStatus() skipped — cloudApiClient not wired")
            return
        }
        val provider = tokenProvider ?: run {
            Log.d(TAG, "pollSyncedToOdooStatus() skipped — tokenProvider not wired")
            return
        }

        if (provider.isDecommissioned()) {
            Log.w(TAG, "pollSyncedToOdooStatus() skipped — device decommissioned")
            return
        }

        // M-08: Circuit breaker check
        if (!statusPollCircuitBreaker.allowRequest()) {
            val waitMs = statusPollCircuitBreaker.remainingBackoffMs()
            Log.d(TAG, "pollSyncedToOdooStatus() skipped — circuit breaker (state=${statusPollCircuitBreaker.state}, ${waitMs}ms remaining)")
            return
        }

        val uploadedIds = bm.getUploadedFccTransactionIds(CLOUD_MAX_BATCH_SIZE)
        if (uploadedIds.isEmpty()) {
            Log.d(TAG, "pollSyncedToOdooStatus() — no UPLOADED records to check")
            return
        }

        val token = provider.getAccessToken() ?: run {
            if (provider.isDecommissioned()) {
                Log.w(TAG, "pollSyncedToOdooStatus() skipped — device decommissioned (detected on token fetch)")
            } else {
                Log.w(TAG, "pollSyncedToOdooStatus() skipped — no access token available")
            }
            return
        }

        Log.i(TAG, "Polling synced-to-Odoo status for ${uploadedIds.size} UPLOADED records")

        val result = doStatusPoll(client, provider, uploadedIds, token)
        handleStatusPollResult(bm, result)
    }

    /**
     * Send accumulated telemetry metrics to cloud.
     * Piggybacks on an existing successful cloud cycle (never permanently hot).
     *
     * Fire-and-forget: if submission fails, the payload is discarded.
     * Error counters are reset only on a successful HTTP 204.
     */
    suspend fun reportTelemetry() {
        val reporter = telemetryReporter ?: run {
            Log.d(TAG, "reportTelemetry() skipped — telemetryReporter not wired")
            return
        }
        val client = cloudApiClient ?: run {
            Log.d(TAG, "reportTelemetry() skipped — cloudApiClient not wired")
            return
        }
        val provider = tokenProvider ?: run {
            Log.d(TAG, "reportTelemetry() skipped — tokenProvider not wired")
            return
        }

        if (provider.isDecommissioned()) {
            Log.w(TAG, "reportTelemetry() skipped — device decommissioned")
            return
        }

        val payload = reporter.buildPayload() ?: run {
            Log.d(TAG, "reportTelemetry() skipped — payload could not be assembled")
            return
        }

        val token = provider.getAccessToken() ?: run {
            if (provider.isDecommissioned()) {
                Log.w(TAG, "reportTelemetry() skipped — device decommissioned (detected on token fetch)")
            } else {
                Log.w(TAG, "reportTelemetry() skipped — no access token available")
            }
            return
        }

        Log.d(TAG, "Submitting telemetry (seq=${payload.sequenceNumber})")

        try {
            when (val result = client.submitTelemetry(payload, token)) {
                is CloudTelemetryResult.Success -> {
                    // M-05: Use atomic snapshot+reset so no increments are lost between
                    // the read in buildPayload() and the reset here.
                    reporter.snapshotAndResetErrorCounts()
                    Log.i(TAG, "Telemetry submitted (seq=${payload.sequenceNumber})")
                }

                is CloudTelemetryResult.Unauthorized -> {
                    // Attempt one token refresh and retry
                    Log.i(TAG, "Telemetry returned 401 — attempting token refresh")
                    val refreshed = provider.refreshAccessToken()
                    if (refreshed) {
                        val freshToken = provider.getAccessToken()
                        if (freshToken != null) {
                            when (client.submitTelemetry(payload, freshToken)) {
                                is CloudTelemetryResult.Success -> {
                                    reporter.snapshotAndResetErrorCounts()
                                    Log.i(TAG, "Telemetry submitted after token refresh (seq=${payload.sequenceNumber})")
                                }
                                else -> {
                                    reporter.cloudAuthErrors.incrementAndGet()
                                    Log.w(TAG, "Telemetry retry failed after token refresh")
                                }
                            }
                        }
                    } else {
                        reporter.cloudAuthErrors.incrementAndGet()
                        Log.w(TAG, "Telemetry token refresh failed")
                    }
                }

                is CloudTelemetryResult.RateLimited -> {
                    Log.w(TAG, "Telemetry rate limited (429); retryAfter=${result.retryAfterSeconds}s — discarding payload")
                }

                is CloudTelemetryResult.Forbidden -> {
                    reporter.cloudAuthErrors.incrementAndGet()
                    Log.w(TAG, "Telemetry forbidden: ${result.errorCode}")
                }

                is CloudTelemetryResult.TransportError -> {
                    Log.w(TAG, "Telemetry submission failed: ${result.message}")
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "Telemetry submission exception", e)
        }
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Status poll helpers
    // -------------------------------------------------------------------------

    /**
     * Execute the status poll HTTP call with 401 → refresh → retry handling.
     */
    private suspend fun doStatusPoll(
        client: CloudApiClient,
        provider: DeviceTokenProvider,
        ids: List<String>,
        token: String,
    ): StatusPollAttemptResult {
        return when (val result = client.getSyncedStatus(ids, token)) {
            is CloudStatusPollResult.Success -> StatusPollAttemptResult.Success(result.response)

            is CloudStatusPollResult.Unauthorized -> {
                Log.i(TAG, "Status poll returned 401 — attempting token refresh")
                val refreshed = provider.refreshAccessToken()
                if (!refreshed) {
                    Log.e(TAG, "Token refresh failed during status poll")
                    return StatusPollAttemptResult.TransportFailure("401 Unauthorized — token refresh failed")
                }
                val freshToken = provider.getAccessToken()
                if (freshToken == null) {
                    // M-04: Critical state bug — refresh reported success but token store is empty/corrupt
                    Log.e(TAG, "CRITICAL: Token refresh succeeded but getAccessToken() returned null — token store may be corrupt")
                    telemetryReporter?.cloudAuthErrors?.incrementAndGet()
                    return StatusPollAttemptResult.TransportFailure("CRITICAL: token store inconsistency — refresh succeeded but no token available")
                }
                Log.i(TAG, "Token refreshed; retrying status poll")
                when (val retryResult = client.getSyncedStatus(ids, freshToken)) {
                    is CloudStatusPollResult.Success -> StatusPollAttemptResult.Success(retryResult.response)
                    is CloudStatusPollResult.Unauthorized ->
                        StatusPollAttemptResult.TransportFailure("401 Unauthorized after token refresh retry")
                    is CloudStatusPollResult.Forbidden ->
                        resolveStatusPollForbidden(retryResult.errorCode)
                    is CloudStatusPollResult.RateLimited ->
                        StatusPollAttemptResult.RateLimited(retryResult.retryAfterSeconds)
                    is CloudStatusPollResult.TransportError ->
                        StatusPollAttemptResult.TransportFailure(retryResult.message)
                }
            }

            is CloudStatusPollResult.RateLimited ->
                StatusPollAttemptResult.RateLimited(result.retryAfterSeconds)

            is CloudStatusPollResult.Forbidden -> resolveStatusPollForbidden(result.errorCode)

            is CloudStatusPollResult.TransportError ->
                StatusPollAttemptResult.TransportFailure(result.message)
        }
    }

    private fun resolveStatusPollForbidden(errorCode: String?): StatusPollAttemptResult =
        if (errorCode == DECOMMISSIONED_ERROR_CODE) {
            StatusPollAttemptResult.Decommissioned
        } else {
            StatusPollAttemptResult.TransportFailure("403 Forbidden: $errorCode")
        }

    /**
     * Process the status poll result: mark SYNCED_TO_ODOO locally, update SyncState, handle errors.
     */
    private suspend fun handleStatusPollResult(
        bm: TransactionBufferManager,
        result: StatusPollAttemptResult,
    ) {
        when (result) {
            is StatusPollAttemptResult.Success -> {
                val syncedIds = mutableListOf<String>()
                var notFoundCount = 0
                var stalePendingCount = 0
                var duplicateCount = 0
                var otherCount = 0

                for (entry in result.response.statuses) {
                    when (entry.status) {
                        SYNCED_TO_ODOO_STATUS -> syncedIds += entry.id

                        STATUS_NOT_FOUND -> {
                            notFoundCount++
                            Log.w(
                                TAG,
                                "Status poll: fccTransactionId='${entry.id}' returned NOT_FOUND — " +
                                    "record may have been lost in cloud or never persisted.",
                            )
                        }

                        STATUS_STALE_PENDING -> {
                            stalePendingCount++
                            Log.w(
                                TAG,
                                "Status poll: fccTransactionId='${entry.id}' returned STALE_PENDING — " +
                                    "record is stuck in cloud processing.",
                            )
                        }

                        STATUS_DUPLICATE -> {
                            duplicateCount++
                            Log.i(
                                TAG,
                                "Status poll: fccTransactionId='${entry.id}' returned DUPLICATE — " +
                                    "cloud dedup confirmed.",
                            )
                        }

                        else -> {
                            otherCount++
                            Log.w(
                                TAG,
                                "Status poll: fccTransactionId='${entry.id}' returned " +
                                    "unrecognised status '${entry.status}'.",
                            )
                        }
                    }
                }

                if (syncedIds.isNotEmpty()) {
                    bm.markSyncedToOdoo(syncedIds)
                    Log.i(TAG, "Marked ${syncedIds.size} records SYNCED_TO_ODOO")
                }

                if (notFoundCount > 0 || stalePendingCount > 0) {
                    Log.w(
                        TAG,
                        "Status poll anomalies: notFound=$notFoundCount stalePending=$stalePendingCount " +
                            "duplicate=$duplicateCount other=$otherCount",
                    )
                    telemetryReporter?.cloudUploadErrors?.addAndGet(notFoundCount + stalePendingCount)
                }

                if (syncedIds.isEmpty() && notFoundCount == 0 && stalePendingCount == 0 &&
                    duplicateCount == 0 && otherCount == 0
                ) {
                    Log.d(TAG, "Status poll returned no actionable entries")
                }

                // M-03: Write SyncState before resetting circuit breaker (same rationale as upload)
                val dbWriteSucceeded = updateLastStatusPollAt()
                if (dbWriteSucceeded) {
                    statusPollCircuitBreaker.recordSuccess()
                } else {
                    Log.w(TAG, "SyncState write failed after successful status poll; circuit breaker NOT reset")
                }
            }

            is StatusPollAttemptResult.RateLimited -> {
                val retryAfter = result.retryAfterSeconds
                if (retryAfter != null) {
                    statusPollCircuitBreaker.setBackoffSeconds(retryAfter)
                    Log.w(TAG, "Status poll rate limited (429); backing off for ${retryAfter}s")
                } else {
                    val backoffMs = statusPollCircuitBreaker.recordFailure()
                    Log.w(TAG, "Status poll rate limited (429); no Retry-After, using backoff ${backoffMs}ms")
                }
            }

            is StatusPollAttemptResult.Decommissioned -> {
                Log.e(
                    TAG,
                    "DEVICE DECOMMISSIONED during status poll. All cloud sync permanently stopped.",
                )
                tokenProvider?.markDecommissioned()
            }

            is StatusPollAttemptResult.TransportFailure -> {
                val backoffMs = statusPollCircuitBreaker.recordFailure()
                Log.w(
                    TAG,
                    "Status poll failed (failure #${statusPollCircuitBreaker.consecutiveFailureCount}, " +
                        "state=${statusPollCircuitBreaker.state}); next retry after ${backoffMs}ms. " +
                        "Error: ${result.message}",
                )
            }
        }
    }

    /** Update [SyncState.lastStatusPollAt] after a successful status poll. Returns true on success. */
    private suspend fun updateLastStatusPollAt(): Boolean {
        val dao = syncStateDao ?: return false
        val now = Instant.now().toString()
        return try {
            val current = dao.get()
            val updated = current?.copy(lastStatusPollAt = now, updatedAt = now)
                ?: SyncState(
                    lastFccCursor = null,
                    lastUploadAt = null,
                    lastStatusPollAt = now,
                    lastConfigPullAt = null,
                    lastConfigVersion = null,
                    updatedAt = now,
                )
            dao.upsert(updated)
            true
        } catch (e: Exception) {
            Log.w(TAG, "Failed to update lastStatusPollAt in SyncState", e)
            false
        }
    }

    // -------------------------------------------------------------------------
    // Upload helpers
    // -------------------------------------------------------------------------

    /**
     * Execute the HTTP upload, handling 401 → refresh → retry in one step.
     *
     * Returns a sealed [UploadAttemptResult] that the caller maps to buffer updates.
     */
    private suspend fun doUpload(
        client: CloudApiClient,
        provider: DeviceTokenProvider,
        batch: List<BufferedTransaction>,
        token: String,
    ): UploadAttemptResult {
        val request = buildUploadRequest(batch, provider)
        return when (val result = client.uploadBatch(request, token)) {
            is CloudUploadResult.Success -> UploadAttemptResult.Success(result.response)

            is CloudUploadResult.Unauthorized -> {
                // Attempt one token refresh then retry
                Log.i(TAG, "Upload returned 401 — attempting token refresh")
                val refreshed = provider.refreshAccessToken()
                if (!refreshed) {
                    Log.e(TAG, "Token refresh failed")
                    return UploadAttemptResult.TransportFailure("401 Unauthorized — token refresh failed")
                }
                val freshToken = provider.getAccessToken()
                if (freshToken == null) {
                    // M-04: This is a critical internal state bug — refresh reported success but
                    // the token store is empty/corrupt. Escalate via telemetry and do NOT apply
                    // transport backoff since the problem is local, not network-related.
                    Log.e(TAG, "CRITICAL: Token refresh succeeded but getAccessToken() returned null — token store may be corrupt")
                    telemetryReporter?.cloudAuthErrors?.incrementAndGet()
                    return UploadAttemptResult.TransportFailure("CRITICAL: token store inconsistency — refresh succeeded but no token available")
                }
                Log.i(TAG, "Token refreshed; retrying upload batch")
                when (val retryResult = client.uploadBatch(request, freshToken)) {
                    is CloudUploadResult.Success -> UploadAttemptResult.Success(retryResult.response)
                    is CloudUploadResult.Unauthorized ->
                        UploadAttemptResult.TransportFailure("401 Unauthorized after token refresh retry")
                    is CloudUploadResult.Forbidden ->
                        resolveForbidden(retryResult.errorCode)
                    is CloudUploadResult.RateLimited ->
                        UploadAttemptResult.RateLimited(retryResult.retryAfterSeconds)
                    is CloudUploadResult.PayloadTooLarge ->
                        UploadAttemptResult.PayloadTooLarge
                    is CloudUploadResult.TransportError ->
                        UploadAttemptResult.TransportFailure(retryResult.message)
                }
            }

            is CloudUploadResult.RateLimited ->
                UploadAttemptResult.RateLimited(result.retryAfterSeconds)

            is CloudUploadResult.PayloadTooLarge ->
                UploadAttemptResult.PayloadTooLarge

            is CloudUploadResult.Forbidden -> resolveForbidden(result.errorCode)

            is CloudUploadResult.TransportError ->
                UploadAttemptResult.TransportFailure(result.message)
        }
    }

    private fun resolveForbidden(errorCode: String?): UploadAttemptResult =
        if (errorCode == DECOMMISSIONED_ERROR_CODE) {
            UploadAttemptResult.Decommissioned
        } else {
            UploadAttemptResult.TransportFailure("403 Forbidden: $errorCode")
        }

    private suspend fun handleUploadResult(
        bm: TransactionBufferManager,
        batch: List<BufferedTransaction>,
        result: UploadAttemptResult,
    ) {
        when (result) {
            is UploadAttemptResult.Success -> {
                processUploadResponse(bm, batch, result.response)
                // M-03: Write SyncState to disk BEFORE resetting in-memory backoff counters.
                val dbWriteSucceeded = updateLastUploadAt()
                if (dbWriteSucceeded) {
                    uploadCircuitBreaker.recordSuccess()
                    // M-15: Reset effective batch size on success
                    effectiveBatchSize = config.uploadBatchSize
                } else {
                    // DB write failed — keep backoff active so the next tick retries the write.
                    Log.w(TAG, "SyncState write failed after successful upload; circuit breaker NOT reset")
                }
            }

            is UploadAttemptResult.RateLimited -> {
                // M-15: Use Retry-After if available, otherwise fall back to circuit breaker backoff.
                // Do not count rate limiting as a failure — it is flow control, not a fault.
                val retryAfter = result.retryAfterSeconds
                if (retryAfter != null) {
                    uploadCircuitBreaker.setBackoffSeconds(retryAfter)
                    Log.w(TAG, "Rate limited (429); backing off for ${retryAfter}s (Retry-After header)")
                } else {
                    val backoffMs = uploadCircuitBreaker.recordFailure()
                    Log.w(TAG, "Rate limited (429); no Retry-After header, using backoff ${backoffMs}ms")
                }
            }

            is UploadAttemptResult.PayloadTooLarge -> {
                // M-15: Halve the effective batch size (floor: 1) and retry on next tick.
                val newSize = (effectiveBatchSize / 2).coerceAtLeast(1)
                Log.w(
                    TAG,
                    "Payload too large (413); reducing batch size from $effectiveBatchSize to $newSize",
                )
                effectiveBatchSize = newSize
                // No circuit breaker penalty — this is a configuration issue, not a transport fault.
            }

            is UploadAttemptResult.Decommissioned -> {
                // Permanent stop — log at error level so diagnostics surface this immediately
                Log.e(
                    TAG,
                    "DEVICE DECOMMISSIONED by cloud. All cloud sync permanently stopped. " +
                        "Re-provisioning is required.",
                )
                tokenProvider?.markDecommissioned()
                // Do not apply retry backoff — decommission is permanent
            }

            is UploadAttemptResult.TransportFailure -> {
                val backoffMs = uploadCircuitBreaker.recordFailure()
                Log.w(
                    TAG,
                    "Batch upload failed (failure #${uploadCircuitBreaker.consecutiveFailureCount}, " +
                        "state=${uploadCircuitBreaker.state}); next retry after ${backoffMs}ms. " +
                        "Error: ${result.message}",
                )
                // Record the failure against every record in the batch so the diagnostics
                // screen and telemetry can surface upload error details.
                val attemptAt = Instant.now().toString()
                for (tx in batch) {
                    bm.recordUploadFailure(
                        id = tx.id,
                        attempts = tx.uploadAttempts + 1,
                        attemptAt = attemptAt,
                        error = result.message,
                    )
                }
            }
        }
    }

    /**
     * Process a successful HTTP 200 response by updating per-record sync status.
     *
     * ACCEPTED  → UPLOADED (cloud persisted a new record)
     * DUPLICATE → UPLOADED (cloud already has this record; local dedup confirmed)
     * REJECTED  → increment attempts, leave PENDING (cloud rejected the record specifically)
     *
     * Per §5.3 Edge Sync State Machine: both ACCEPTED and DUPLICATE map to UPLOADED.
     * This ensures rejected records are never skipped — they remain in the PENDING queue
     * for the next upload cycle while upload_attempts tracks the history.
     */
    private suspend fun processUploadResponse(
        bm: TransactionBufferManager,
        batch: List<BufferedTransaction>,
        response: CloudUploadResponse,
    ) {
        val uploadedIds = mutableListOf<String>()
        val rejectedPairs = mutableListOf<Pair<BufferedTransaction, String>>()

        // O(1) lookup: fccTransactionId → local record
        val batchByFccId = batch.associateBy { it.fccTransactionId }

        var unmatchedCount = 0

        for (result in response.results) {
            val local = batchByFccId[result.fccTransactionId]
            if (local == null) {
                unmatchedCount++
                Log.w(
                    TAG,
                    "Cloud returned result for fccTransactionId='${result.fccTransactionId}' " +
                        "which does not match any record in the local batch. " +
                        "Outcome=${result.outcome}. Record may be orphaned in PENDING state.",
                )
                continue
            }
            when (result.outcome) {
                UploadOutcome.ACCEPTED.name,
                UploadOutcome.DUPLICATE.name -> uploadedIds += local.id

                UploadOutcome.REJECTED.name -> {
                    val errorMsg = result.error
                        ?.let { "${it.errorCode}: ${it.message}" }
                        ?: "REJECTED (no error detail)"
                    rejectedPairs += local to errorMsg
                }

                else -> {
                    Log.w(
                        TAG,
                        "Cloud returned unknown outcome '${result.outcome}' for " +
                            "fccTransactionId='${result.fccTransactionId}'. Record left PENDING.",
                    )
                }
            }
        }

        if (unmatchedCount > 0) {
            Log.e(
                TAG,
                "$unmatchedCount cloud response result(s) did not match any local batch record. " +
                    "Batch had ${batch.size} records, response had ${response.results.size} results. " +
                    "Unmatched records remain PENDING and may be orphaned.",
            )
            telemetryReporter?.cloudUploadErrors?.addAndGet(unmatchedCount)
        }

        if (uploadedIds.isNotEmpty()) {
            bm.markUploaded(uploadedIds)
            Log.i(TAG, "Marked ${uploadedIds.size} records UPLOADED")
        }

        if (rejectedPairs.isNotEmpty()) {
            val attemptAt = Instant.now().toString()
            for ((tx, error) in rejectedPairs) {
                Log.w(TAG, "Record ${tx.fccTransactionId} REJECTED by cloud: $error")
                bm.recordUploadFailure(
                    id = tx.id,
                    attempts = tx.uploadAttempts + 1,
                    attemptAt = attemptAt,
                    error = error,
                )
            }
        }

        Log.i(
            TAG,
            "Upload response processed: accepted=${response.acceptedCount} " +
                "duplicate=${response.duplicateCount} rejected=${response.rejectedCount}",
        )
    }

    /**
     * Build the [CloudUploadRequest] from the local batch.
     *
     * [legalEntityId] comes from the device JWT's `lei` claim via [DeviceTokenProvider].
     * If not available (not yet provisioned), uses an empty string — the cloud will
     * reject such a request with a validation error, which increments upload_attempts.
     *
     * Transactions are already ordered oldest-first from [TransactionBufferManager.getPendingBatch].
     */
    private fun buildUploadRequest(
        batch: List<BufferedTransaction>,
        provider: DeviceTokenProvider,
    ): CloudUploadRequest {
        val legalEntityId = provider.getLegalEntityId() ?: ""
        return CloudUploadRequest(
            transactions = batch.map { tx -> tx.toDto(legalEntityId) },
        )
    }

    private fun BufferedTransaction.toDto(legalEntityId: String): CloudTransactionDto =
        CloudTransactionDto(
            id = id,
            fccTransactionId = fccTransactionId,
            siteCode = siteCode,
            pumpNumber = pumpNumber,
            nozzleNumber = nozzleNumber,
            productCode = productCode,
            volumeMicrolitres = volumeMicrolitres,
            amountMinorUnits = amountMinorUnits,
            unitPriceMinorPerLitre = unitPriceMinorPerLitre,
            currencyCode = currencyCode,
            startedAt = startedAt,
            completedAt = completedAt,
            fccVendor = fccVendor,
            legalEntityId = legalEntityId,
            status = status,
            ingestionSource = ingestionSource,
            ingestedAt = createdAt,   // createdAt = ingestedAt per BufferManager.toEntity()
            updatedAt = updatedAt,
            schemaVersion = schemaVersion,
            isDuplicate = false,
            correlationId = correlationId,
            fiscalReceiptNumber = fiscalReceiptNumber,
            attendantId = attendantId,
            rawPayloadJson = rawPayloadJson,
        )

    /** Update [SyncState.lastUploadAt] after a successful batch upload. Returns true on success. */
    private suspend fun updateLastUploadAt(): Boolean {
        val dao = syncStateDao ?: return false
        val now = Instant.now().toString()
        return try {
            val current = dao.get()
            val updated = current?.copy(lastUploadAt = now, updatedAt = now)
                ?: SyncState(
                    lastFccCursor = null,
                    lastUploadAt = now,
                    lastStatusPollAt = null,
                    lastConfigPullAt = null,
                    lastConfigVersion = null,
                    updatedAt = now,
                )
            dao.upsert(updated)
            true
        } catch (e: Exception) {
            Log.w(TAG, "Failed to update lastUploadAt in SyncState", e)
            false
        }
    }

    // -------------------------------------------------------------------------
    // Internal sealed result — keeps API surface details out of public interfaces
    // -------------------------------------------------------------------------

    private sealed class UploadAttemptResult {
        data class Success(val response: CloudUploadResponse) : UploadAttemptResult()
        data object Decommissioned : UploadAttemptResult()
        data class RateLimited(val retryAfterSeconds: Long?) : UploadAttemptResult()
        data object PayloadTooLarge : UploadAttemptResult()
        data class TransportFailure(val message: String) : UploadAttemptResult()
    }

    private sealed class StatusPollAttemptResult {
        data class Success(val response: SyncedStatusResponse) : StatusPollAttemptResult()
        data object Decommissioned : StatusPollAttemptResult()
        data class RateLimited(val retryAfterSeconds: Long?) : StatusPollAttemptResult()
        data class TransportFailure(val message: String) : StatusPollAttemptResult()
    }
}

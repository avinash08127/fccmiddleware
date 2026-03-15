package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.logging.StructuredFileLogger
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
 * AT-013: Core dependencies are non-nullable — Koin provides all of them as
 * non-null singletons. Only [telemetryReporter] and [fileLogger] remain nullable
 * as they are genuinely optional (diagnostics-only).
 */
class CloudUploadWorker(
    private val bufferManager: TransactionBufferManager,
    private val syncStateDao: SyncStateDao,
    private val cloudApiClient: CloudApiClient,
    private val tokenProvider: DeviceTokenProvider,
    val config: CloudUploadWorkerConfig = CloudUploadWorkerConfig(),
    private val telemetryReporter: TelemetryReporter? = null,
    private val fileLogger: StructuredFileLogger? = null,
    private val configManager: ConfigManager,
) {

    /**
     * P2-08: Callback invoked when a cloud response indicates the peer directory is stale.
     * Wired by [CadenceController] to trigger [CadenceController.triggerImmediateConfigPoll].
     */
    var onPeerDirectoryStale: (() -> Unit)? = null

    companion object {
        private const val TAG = "CloudUploadWorker"
        private const val DECOMMISSIONED_ERROR_CODE = "DEVICE_DECOMMISSIONED"
        private const val DEFAULT_STATUS_POLL_SINCE = "1970-01-01T00:00:00Z"

        /** Cloud API rejects batches larger than this. */
        private const val CLOUD_MAX_BATCH_SIZE = 500

        /** NET-003: UUID format validation for identity fields sent to cloud. */
        private val UUID_REGEX =
            Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")

        /**
         * NET-009: Max estimated request payload size in bytes (2 MB).
         * Proactively limits batch size before serialization to avoid OOM on
         * constrained devices and timeouts on throttled mobile connections.
         */
        private const val MAX_ESTIMATED_PAYLOAD_BYTES = 2 * 1024 * 1024

        /** NET-009: Estimated base JSON size per transaction record (without rawPayloadJson). */
        private const val ESTIMATED_BASE_RECORD_BYTES = 600
    }

    /**
     * M-15: Effective batch size — starts at config value, halved on 413 PayloadTooLarge,
     * reset to config value on successful upload. Floor: 1 record.
     */
    @Volatile
    internal var effectiveBatchSize: Int = config.uploadBatchSize.coerceAtLeast(1)

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
        if (tokenProvider.isDecommissioned()) {
            AppLogger.w(TAG, "uploadPendingBatch() skipped — device decommissioned; no further sync")
            return
        }

        // M-08: Circuit breaker check — skip if backoff active or circuit is OPEN
        if (!uploadCircuitBreaker.allowRequest()) {
            val waitMs = uploadCircuitBreaker.remainingBackoffMs()
            AppLogger.d(TAG, "uploadPendingBatch() skipped — circuit breaker (state=${uploadCircuitBreaker.state}, ${waitMs}ms remaining)")
            return
        }

        val batchSize = effectiveBatchSize.coerceIn(1, CLOUD_MAX_BATCH_SIZE)
        val queriedBatch = bufferManager.getPendingBatch(batchSize)
        if (queriedBatch.isEmpty()) {
            // H-02 fix: reset backoff when buffer drains so new records upload immediately
            uploadCircuitBreaker.recordSuccess()
            AppLogger.d(TAG, "uploadPendingBatch() — no PENDING records, circuit breaker reset")
            return
        }

        // NET-009: Proactively trim batch if estimated payload exceeds size limit.
        // This prevents OOM on constrained devices and timeouts on throttled connections.
        val batch = trimBatchByEstimatedSize(queriedBatch)
        val batchWasFull = batch.size == batchSize

        AppLogger.i(TAG, "Starting upload batch: ${batch.size} records")

        val token = tokenProvider.getAccessToken() ?: run {
            // H-05: Re-check decommission status — if device was decommissioned between
            // the initial check and getAccessToken(), a null token is expected and permanent.
            if (tokenProvider.isDecommissioned()) {
                AppLogger.w(TAG, "uploadPendingBatch() skipped — device decommissioned (detected on token fetch)")
            } else {
                AppLogger.w(TAG, "uploadPendingBatch() skipped — no access token available (not provisioned)")
            }
            return
        }

        // AF-008: Skip upload when legalEntityId is unavailable to prevent
        // needless REJECTED responses that increment uploadAttempts and eventually
        // dead-letter all records.
        val legalEntityId = tokenProvider.getLegalEntityId()
        if (legalEntityId.isNullOrEmpty()) {
            AppLogger.w(
                TAG,
                "uploadPendingBatch() skipped — legalEntityId is unavailable (JWT not decoded or lei claim missing). " +
                    "${batch.size} record(s) remain PENDING without incrementing upload attempts.",
            )
            return
        }

        // AF-035: Record the upload attempt timestamp BEFORE the HTTP call, regardless of outcome.
        // This allows telemetry to report when the last attempt was made, even if it failed.
        updateLastUploadAttemptAt()

        val result = doUpload(cloudApiClient, tokenProvider, batch, token)
        handleUploadResult(bufferManager, batch, result, batchWasFull)
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
     * 2. Load the last successful status-poll watermark from `SyncState.lastStatusPollAt`
     *    (or the Unix epoch on first poll).
     * 3. Call `GET /api/v1/transactions/synced-status?since=...` with device JWT.
     * 4. Intersect the returned FCC IDs with the current local UPLOADED set, then call
     *    `bufferManager.markSyncedToOdoo()` for the matches.
     * 5. Persist the poll-start watermark back to `SyncState.lastStatusPollAt`.
     *
     * The cloud endpoint only returns IDs that have newly reached `SYNCED_TO_ODOO` since
     * the supplied timestamp; it does not expose per-record intermediate or failure states.
     * The worker therefore uses an overlap window keyed by the previous poll start time so
     * late acknowledgements are not missed if they land during the request.
     *
     * On HTTP 401: attempt one token refresh and retry.
     * On 403 DEVICE_DECOMMISSIONED: stop all sync permanently.
     * On transport failure: apply exponential backoff, retry on next cadence tick.
     */
    suspend fun pollSyncedToOdooStatus() {
        if (tokenProvider.isDecommissioned()) {
            AppLogger.w(TAG, "pollSyncedToOdooStatus() skipped — device decommissioned")
            return
        }

        // M-08: Circuit breaker check
        if (!statusPollCircuitBreaker.allowRequest()) {
            val waitMs = statusPollCircuitBreaker.remainingBackoffMs()
            AppLogger.d(TAG, "pollSyncedToOdooStatus() skipped — circuit breaker (state=${statusPollCircuitBreaker.state}, ${waitMs}ms remaining)")
            return
        }

        val uploadedIds = bufferManager.getUploadedFccTransactionIds(CLOUD_MAX_BATCH_SIZE)
        if (uploadedIds.isEmpty()) {
            AppLogger.d(TAG, "pollSyncedToOdooStatus() — no UPLOADED records to check")
            return
        }

        val token = tokenProvider.getAccessToken() ?: run {
            if (tokenProvider.isDecommissioned()) {
                AppLogger.w(TAG, "pollSyncedToOdooStatus() skipped — device decommissioned (detected on token fetch)")
            } else {
                AppLogger.w(TAG, "pollSyncedToOdooStatus() skipped — no access token available")
            }
            return
        }

        val since = getStatusPollSince()
        val pollStartedAt = Instant.now().toString()
        AppLogger.i(
            TAG,
            "Polling synced-to-Odoo status for ${uploadedIds.size} UPLOADED records since $since",
        )

        val result = doStatusPoll(cloudApiClient, tokenProvider, since, token)
        handleStatusPollResult(
            bm = bufferManager,
            uploadedIds = uploadedIds,
            pollStartedAt = pollStartedAt,
            result = result,
        )
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
            AppLogger.d(TAG, "reportTelemetry() skipped — telemetryReporter not wired")
            return
        }

        if (tokenProvider.isDecommissioned()) {
            AppLogger.w(TAG, "reportTelemetry() skipped — device decommissioned")
            return
        }

        val payload = reporter.buildPayload() ?: run {
            AppLogger.d(TAG, "reportTelemetry() skipped — payload could not be assembled")
            return
        }

        val token = tokenProvider.getAccessToken() ?: run {
            if (tokenProvider.isDecommissioned()) {
                AppLogger.w(TAG, "reportTelemetry() skipped — device decommissioned (detected on token fetch)")
            } else {
                AppLogger.w(TAG, "reportTelemetry() skipped — no access token available")
            }
            return
        }

        AppLogger.d(TAG, "Submitting telemetry (seq=${payload.sequenceNumber})")

        try {
            when (val result = cloudApiClient.submitTelemetry(payload, token)) {
                is CloudTelemetryResult.Success -> {
                    // AF-037: Error counters are now atomically reset inside buildPayload()
                    // via snapshotAndResetErrorCounts(), eliminating the race window.
                    AppLogger.i(TAG, "Telemetry submitted (seq=${payload.sequenceNumber})")
                }

                is CloudTelemetryResult.Unauthorized -> {
                    // Attempt one token refresh and retry
                    AppLogger.i(TAG, "Telemetry returned 401 — attempting token refresh")
                    val refreshed = tokenProvider.refreshAccessToken()
                    if (refreshed) {
                        val freshToken = tokenProvider.getAccessToken()
                        if (freshToken != null) {
                            when (cloudApiClient.submitTelemetry(payload, freshToken)) {
                                is CloudTelemetryResult.Success -> {
                                    AppLogger.i(TAG, "Telemetry submitted after token refresh (seq=${payload.sequenceNumber})")
                                }
                                else -> {
                                    reporter.cloudAuthErrors.incrementAndGet()
                                    AppLogger.w(TAG, "Telemetry retry failed after token refresh")
                                }
                            }
                        }
                    } else {
                        reporter.cloudAuthErrors.incrementAndGet()
                        AppLogger.w(TAG, "Telemetry token refresh failed")
                    }
                }

                is CloudTelemetryResult.RateLimited -> {
                    AppLogger.w(TAG, "Telemetry rate limited (429); retryAfter=${result.retryAfterSeconds}s — discarding payload")
                }

                is CloudTelemetryResult.Forbidden -> {
                    reporter.cloudAuthErrors.incrementAndGet()
                    AppLogger.w(TAG, "Telemetry forbidden: ${result.errorCode}")
                }

                is CloudTelemetryResult.BadRequest -> {
                    AppLogger.w(TAG, "Telemetry rejected (400): ${result.errorCode} — ${result.message}")
                }

                is CloudTelemetryResult.NotFound -> {
                    AppLogger.w(TAG, "Telemetry device not found (404): ${result.errorCode}")
                }

                is CloudTelemetryResult.TransportError -> {
                    AppLogger.w(TAG, "Telemetry submission failed: ${result.message}")
                }
            }
        } catch (e: Exception) {
            AppLogger.e(TAG, "Telemetry submission exception", e)
        }
    }

    // -------------------------------------------------------------------------
    // Phase 3B: Diagnostic log upload
    // -------------------------------------------------------------------------

    /**
     * Upload recent WARN/ERROR log entries to cloud when config enables it.
     *
     * Only fires when [TelemetryDto.includeDiagnosticsLogs] is true.
     * Reads from [StructuredFileLogger], max 200 entries, fire-and-forget.
     */
    suspend fun reportDiagnosticLogs() {
        val logger = fileLogger ?: return
        val cfg = configManager.config.value ?: return

        if (!cfg.telemetry.includeDiagnosticsLogs) return
        if (tokenProvider.isDecommissioned()) return

        val entries = logger.getRecentDiagnosticEntries(200)
        if (entries.isEmpty()) return

        // NET-003: Validate UUID format before submission to avoid silent 400 from backend.
        val deviceId = cfg.identity.deviceId
        val legalEntityId = cfg.identity.legalEntityId
        if (!UUID_REGEX.matches(deviceId) || !UUID_REGEX.matches(legalEntityId)) {
            AppLogger.e(TAG, "reportDiagnosticLogs() — invalid UUID: deviceId=$deviceId, legalEntityId=$legalEntityId")
            return
        }

        val token = tokenProvider.getAccessToken() ?: return

        val request = DiagnosticLogUploadRequest(
            deviceId = cfg.identity.deviceId,
            siteCode = cfg.identity.siteCode,
            legalEntityId = cfg.identity.legalEntityId,
            uploadedAtUtc = Instant.now().toString(),
            logEntries = entries,
        )

        try {
            when (val result = cloudApiClient.submitDiagnosticLogs(request, token)) {
                is CloudDiagnosticLogResult.Success ->
                    AppLogger.i(TAG, "Diagnostic logs uploaded: ${entries.size} entries")
                is CloudDiagnosticLogResult.Unauthorized -> {
                    // NET-010: Attempt one token refresh and retry, consistent with
                    // reportTelemetry(), uploadPendingBatch(), and all other workers.
                    AppLogger.i(TAG, "Diagnostic log upload returned 401 — attempting token refresh")
                    val refreshed = tokenProvider.refreshAccessToken()
                    if (refreshed) {
                        val freshToken = tokenProvider.getAccessToken()
                        if (freshToken != null) {
                            when (cloudApiClient.submitDiagnosticLogs(request, freshToken)) {
                                is CloudDiagnosticLogResult.Success ->
                                    AppLogger.i(TAG, "Diagnostic logs uploaded after token refresh: ${entries.size} entries")
                                else ->
                                    AppLogger.w(TAG, "Diagnostic log upload retry failed after token refresh")
                            }
                        }
                    } else {
                        AppLogger.w(TAG, "Diagnostic log upload token refresh failed")
                    }
                }
                is CloudDiagnosticLogResult.Forbidden ->
                    AppLogger.w(TAG, "Diagnostic log upload forbidden: ${result.errorCode}")
                is CloudDiagnosticLogResult.BadRequest ->
                    AppLogger.w(TAG, "Diagnostic log upload rejected (400): ${result.errorCode}")
                is CloudDiagnosticLogResult.NotFound ->
                    AppLogger.w(TAG, "Diagnostic log upload device not found (404): ${result.errorCode}")
                is CloudDiagnosticLogResult.TransportError ->
                    AppLogger.w(TAG, "Diagnostic log upload failed: ${result.message}")
            }
        } catch (e: Exception) {
            AppLogger.w(TAG, "Diagnostic log upload exception: ${e.message}")
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
        since: String,
        token: String,
    ): StatusPollAttemptResult {
        return when (val result = client.getSyncedStatus(since, token)) {
            is CloudStatusPollResult.Success -> StatusPollAttemptResult.Success(result.response)

            is CloudStatusPollResult.Unauthorized -> {
                AppLogger.i(TAG, "Status poll returned 401 — attempting token refresh")
                val refreshed = provider.refreshAccessToken()
                if (!refreshed) {
                    AppLogger.e(TAG, "Token refresh failed during status poll")
                    return StatusPollAttemptResult.TransportFailure("401 Unauthorized — token refresh failed")
                }
                val freshToken = provider.getAccessToken()
                if (freshToken == null) {
                    // M-04: Critical state bug — refresh reported success but token store is empty/corrupt
                    AppLogger.e(TAG, "CRITICAL: Token refresh succeeded but getAccessToken() returned null — token store may be corrupt")
                    telemetryReporter?.cloudAuthErrors?.incrementAndGet()
                    return StatusPollAttemptResult.TransportFailure("CRITICAL: token store inconsistency — refresh succeeded but no token available")
                }
                AppLogger.i(TAG, "Token refreshed; retrying status poll")
                when (val retryResult = client.getSyncedStatus(since, freshToken)) {
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
        uploadedIds: List<String>,
        pollStartedAt: String,
        result: StatusPollAttemptResult,
    ) {
        when (result) {
            is StatusPollAttemptResult.Success -> {
                val uploadedSet = uploadedIds.toHashSet()
                val syncedIds = result.response.fccTransactionIds
                    .asSequence()
                    .filter { it in uploadedSet }
                    .distinct()
                    .toList()
                val unmatchedCloudIds = result.response.fccTransactionIds.size - syncedIds.size

                if (syncedIds.isNotEmpty()) {
                    bm.markSyncedToOdoo(syncedIds)
                    AppLogger.i(TAG, "Marked ${syncedIds.size} records SYNCED_TO_ODOO")
                }

                if (unmatchedCloudIds > 0) {
                    AppLogger.d(
                        TAG,
                        "Status poll returned $unmatchedCloudIds synced FCC ID(s) that are no longer " +
                            "locally UPLOADED; ignoring them.",
                    )
                }

                if (syncedIds.isEmpty()) {
                    AppLogger.d(TAG, "Status poll returned no actionable entries")
                }

                // M-03: Write SyncState before resetting circuit breaker (same rationale as upload)
                val dbWriteSucceeded = updateLastStatusPollAt(pollStartedAt)
                if (dbWriteSucceeded) {
                    statusPollCircuitBreaker.recordSuccess()
                } else {
                    AppLogger.w(TAG, "SyncState write failed after successful status poll; circuit breaker NOT reset")
                }
            }

            is StatusPollAttemptResult.RateLimited -> {
                val retryAfter = result.retryAfterSeconds
                if (retryAfter != null) {
                    statusPollCircuitBreaker.setBackoffSeconds(retryAfter)
                    AppLogger.w(TAG, "Status poll rate limited (429); backing off for ${retryAfter}s")
                } else {
                    val backoffMs = statusPollCircuitBreaker.recordFailure()
                    AppLogger.w(TAG, "Status poll rate limited (429); no Retry-After, using backoff ${backoffMs}ms")
                }
            }

            is StatusPollAttemptResult.Decommissioned -> {
                AppLogger.e(
                    TAG,
                    "DEVICE DECOMMISSIONED during status poll. All cloud sync permanently stopped.",
                )
                tokenProvider.markDecommissioned()
            }

            is StatusPollAttemptResult.TransportFailure -> {
                val backoffMs = statusPollCircuitBreaker.recordFailure()
                // AT-007: Use snapshot() to read state atomically under the mutex
                val snap = statusPollCircuitBreaker.snapshot()
                AppLogger.w(
                    TAG,
                    "Status poll failed (failure #${snap.consecutiveFailureCount}, " +
                        "state=${snap.state}); next retry after ${backoffMs}ms. " +
                        "Error: ${result.message}",
                )
            }
        }
    }

    /** AT-035: Update [SyncState.lastStatusPollAt] atomically without read-modify-write. Returns true on success. */
    private suspend fun updateLastStatusPollAt(pollStartedAt: String): Boolean {
        return try {
            syncStateDao.updateStatusPollAt(pollStartedAt)
            true
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to update lastStatusPollAt in SyncState", e)
            false
        }
    }

    private suspend fun getStatusPollSince(): String =
        syncStateDao.get()?.lastStatusPollAt ?: DEFAULT_STATUS_POLL_SINCE

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
            is CloudUploadResult.Success -> {
                // P2-08: Check peer directory version from upload response
                checkPeerDirectoryVersion(result.peerDirectoryVersion)
                UploadAttemptResult.Success(result.response)
            }

            is CloudUploadResult.Unauthorized -> {
                // Attempt one token refresh then retry
                AppLogger.i(TAG, "Upload returned 401 — attempting token refresh")
                val refreshed = provider.refreshAccessToken()
                if (!refreshed) {
                    AppLogger.e(TAG, "Token refresh failed")
                    return UploadAttemptResult.TransportFailure("401 Unauthorized — token refresh failed")
                }
                val freshToken = provider.getAccessToken()
                if (freshToken == null) {
                    // M-04: This is a critical internal state bug — refresh reported success but
                    // the token store is empty/corrupt. Escalate via telemetry and do NOT apply
                    // transport backoff since the problem is local, not network-related.
                    AppLogger.e(TAG, "CRITICAL: Token refresh succeeded but getAccessToken() returned null — token store may be corrupt")
                    telemetryReporter?.cloudAuthErrors?.incrementAndGet()
                    return UploadAttemptResult.TransportFailure("CRITICAL: token store inconsistency — refresh succeeded but no token available")
                }
                AppLogger.i(TAG, "Token refreshed; retrying upload batch")
                when (val retryResult = client.uploadBatch(request, freshToken)) {
                    is CloudUploadResult.Success -> {
                        checkPeerDirectoryVersion(retryResult.peerDirectoryVersion)
                        UploadAttemptResult.Success(retryResult.response)
                    }
                    is CloudUploadResult.Unauthorized ->
                        UploadAttemptResult.TransportFailure("401 Unauthorized after token refresh retry")
                    is CloudUploadResult.Forbidden ->
                        resolveForbidden(retryResult.errorCode)
                    is CloudUploadResult.RateLimited ->
                        UploadAttemptResult.RateLimited(retryResult.retryAfterSeconds)
                    is CloudUploadResult.Conflict -> {
                        AppLogger.w(TAG, "Upload 409 conflict after retry: ${retryResult.errorCode} — ${retryResult.message}")
                        UploadAttemptResult.TransportFailure("409 Conflict: ${retryResult.errorCode} — HA fencing rejected upload")
                    }
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

            is CloudUploadResult.Conflict -> {
                AppLogger.w(TAG, "Upload 409 conflict: ${result.errorCode} — ${result.message}")
                UploadAttemptResult.TransportFailure("409 Conflict: ${result.errorCode} — HA fencing rejected upload")
            }

            is CloudUploadResult.TransportError ->
                UploadAttemptResult.TransportFailure(result.message)
        }
    }

    /** P2-08: Check if cloud's peer directory version indicates staleness and trigger config refresh. */
    private fun checkPeerDirectoryVersion(cloudVersion: Long?) {
        if (cloudVersion == null) return
        if (configManager.isPeerDirectoryStale(cloudVersion)) {
            AppLogger.i(TAG, "Peer directory stale: cloud=$cloudVersion > local=${configManager.currentPeerDirectoryVersion}")
            configManager.updatePeerDirectoryVersion(cloudVersion)
            onPeerDirectoryStale?.invoke()
        } else {
            configManager.updatePeerDirectoryVersion(cloudVersion)
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
        batchWasFull: Boolean = false,
    ) {
        when (result) {
            is UploadAttemptResult.Success -> {
                processUploadResponse(bm, batch, result.response)
                // M-03: Write SyncState to disk BEFORE resetting in-memory backoff counters.
                val dbWriteSucceeded = updateLastUploadAt()
                if (dbWriteSucceeded) {
                    uploadCircuitBreaker.recordSuccess()
                    // P-002: Adaptive batch sizing — grow on full batch (backlog present),
                    // reset to config default on partial batch (backlog cleared).
                    effectiveBatchSize = if (batchWasFull) {
                        val boosted = (effectiveBatchSize * 2).coerceAtMost(CLOUD_MAX_BATCH_SIZE)
                        if (boosted > effectiveBatchSize) {
                            AppLogger.d(TAG, "Full batch uploaded; boosting effectiveBatchSize $effectiveBatchSize → $boosted")
                        }
                        boosted
                    } else {
                        config.uploadBatchSize.coerceAtLeast(1)
                    }
                } else {
                    // DB write failed — keep backoff active so the next tick retries the write.
                    AppLogger.w(TAG, "SyncState write failed after successful upload; circuit breaker NOT reset")
                }
            }

            is UploadAttemptResult.RateLimited -> {
                // M-15: Use Retry-After if available, otherwise fall back to circuit breaker backoff.
                // Do not count rate limiting as a failure — it is flow control, not a fault.
                val retryAfter = result.retryAfterSeconds
                if (retryAfter != null) {
                    uploadCircuitBreaker.setBackoffSeconds(retryAfter)
                    AppLogger.w(TAG, "Rate limited (429); backing off for ${retryAfter}s (Retry-After header)")
                } else {
                    val backoffMs = uploadCircuitBreaker.recordFailure()
                    AppLogger.w(TAG, "Rate limited (429); no Retry-After header, using backoff ${backoffMs}ms")
                }
            }

            is UploadAttemptResult.PayloadTooLarge -> {
                // M-15: Halve the effective batch size (floor: 1) and retry on next tick.
                val originalSize = effectiveBatchSize
                val newSize = (originalSize / 2).coerceAtLeast(1)
                AppLogger.w(
                    TAG,
                    "Payload too large (413); reducing batch size from $originalSize to $newSize " +
                        "(config max=${config.uploadBatchSize}, batch records=${batch.size})",
                )
                effectiveBatchSize = newSize
                // No circuit breaker penalty — this is a configuration issue, not a transport fault.
            }

            is UploadAttemptResult.Decommissioned -> {
                // Permanent stop — log at error level so diagnostics surface this immediately
                AppLogger.e(
                    TAG,
                    "DEVICE DECOMMISSIONED by cloud. All cloud sync permanently stopped. " +
                        "Re-provisioning is required.",
                )
                tokenProvider.markDecommissioned()
                // Do not apply retry backoff — decommission is permanent
            }

            is UploadAttemptResult.TransportFailure -> {
                val backoffMs = uploadCircuitBreaker.recordFailure()
                // AT-007: Use snapshot() to read state atomically under the mutex
                val snap = uploadCircuitBreaker.snapshot()
                AppLogger.w(
                    TAG,
                    "Batch upload failed (failure #${snap.consecutiveFailureCount}, " +
                        "state=${snap.state}); next retry after ${backoffMs}ms. " +
                        "Error: ${result.message}",
                )
                // AP-033: Record the failure against the entire batch in a single UPDATE
                // instead of N individual UPDATEs. Reduces 50–250ms of sequential SQLite
                // I/O to a single ~2–5ms batch operation.
                val attemptAt = Instant.now().toString()
                bm.recordBatchUploadFailure(
                    ids = batch.map { it.id },
                    attemptAt = attemptAt,
                    error = result.message,
                )
                // H-11: Dead-letter records that have exhausted all upload retries after
                // transport failures too, not just after cloud REJECTED responses.
                bm.deadLetterExhausted()
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
                AppLogger.w(
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
                    val errorMsg = listOfNotNull(result.errorCode, result.errorMessage)
                        .joinToString(": ")
                        .ifBlank { "REJECTED (no error detail)" }
                    rejectedPairs += local to errorMsg
                }

                else -> {
                    AppLogger.w(
                        TAG,
                        "Cloud returned unknown outcome '${result.outcome}' for " +
                            "fccTransactionId='${result.fccTransactionId}'. Record left PENDING.",
                    )
                }
            }
        }

        if (unmatchedCount > 0) {
            AppLogger.e(
                TAG,
                "$unmatchedCount cloud response result(s) did not match any local batch record. " +
                    "Batch had ${batch.size} records, response had ${response.results.size} results. " +
                    "Unmatched records remain PENDING and may be orphaned.",
            )
            telemetryReporter?.cloudUploadErrors?.addAndGet(unmatchedCount)
        }

        if (uploadedIds.isNotEmpty()) {
            bm.markUploaded(uploadedIds)
            AppLogger.i(TAG, "Marked ${uploadedIds.size} records UPLOADED")
        }

        if (rejectedPairs.isNotEmpty()) {
            val attemptAt = Instant.now().toString()
            for ((tx, error) in rejectedPairs) {
                val newAttempts = tx.uploadAttempts + 1
                AppLogger.w(TAG, "Record ${tx.fccTransactionId} REJECTED by cloud (attempt $newAttempts): $error")
                bm.recordUploadFailure(
                    id = tx.id,
                    attempts = newAttempts,
                    attemptAt = attemptAt,
                    error = error,
                )
            }
            // GAP-1: Dead-letter records that have exhausted all upload retries.
            // This runs after recording failures so the attempt counts are up to date.
            bm.deadLetterExhausted()
        }

        AppLogger.i(
            TAG,
            "Upload response processed: accepted=${response.acceptedCount} " +
                "duplicate=${response.duplicateCount} rejected=${response.rejectedCount}",
        )
    }

    /**
     * Build the [CloudUploadRequest] from the local batch.
     *
     * [legalEntityId] comes from the device JWT's `lei` claim via [DeviceTokenProvider].
     * AF-008: Callers must ensure legalEntityId is non-null/non-empty before calling
     * this method (validated in uploadPendingBatch).
     *
     * Transactions are already ordered oldest-first from [TransactionBufferManager.getPendingBatch].
     */
    private fun buildUploadRequest(
        batch: List<BufferedTransaction>,
        provider: DeviceTokenProvider,
    ): CloudUploadRequest {
        // AF-008: legalEntityId is validated by uploadPendingBatch() before reaching here.
        // The fallback to empty string is retained only as a defensive measure.
        val legalEntityId = provider.getLegalEntityId() ?: ""
        val batchId = java.util.UUID.randomUUID().toString()
        // AP-003: Only include raw payloads when config opts in via buffer.persistRawPayloads.
        val includeRawPayload = configManager.config.value?.buffer?.persistRawPayloads ?: false
        val leaderEpoch = configManager.config.value?.siteHa?.leaderEpoch?.takeIf { it > 0 }
        AppLogger.i(TAG, "Upload batch: batchId=$batchId records=${batch.size}")
        return CloudUploadRequest(
            transactions = batch.map { tx -> tx.toDto(legalEntityId, includeRawPayload) },
            leaderEpoch = leaderEpoch,
            uploadBatchId = batchId,
        )
    }

    private fun BufferedTransaction.toDto(legalEntityId: String, includeRawPayload: Boolean): CloudTransactionDto =
        CloudTransactionDto(
            fccTransactionId = fccTransactionId,
            siteCode = siteCode,
            fccVendor = fccVendor,
            pumpNumber = pumpNumber,
            nozzleNumber = nozzleNumber,
            productCode = productCode,
            volumeMicrolitres = volumeMicrolitres,
            amountMinorUnits = amountMinorUnits,
            unitPriceMinorPerLitre = unitPriceMinorPerLitre,
            currencyCode = currencyCode,
            startedAt = startedAt,
            completedAt = completedAt,
            fccCorrelationId = fccCorrelationId,
            odooOrderId = odooOrderId,
            fiscalReceiptNumber = fiscalReceiptNumber,
            attendantId = attendantId,
        )

    /**
     * AF-035 / AT-035: Update [SyncState.lastUploadAttemptAt] atomically at the START
     * of each upload attempt, without read-modify-write.
     */
    private suspend fun updateLastUploadAttemptAt() {
        val now = Instant.now().toString()
        try {
            syncStateDao.updateUploadAttemptAt(now)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to update lastUploadAttemptAt in SyncState", e)
        }
    }

    /** AT-035: Update [SyncState.lastUploadAt] atomically without read-modify-write. Returns true on success. */
    private suspend fun updateLastUploadAt(): Boolean {
        val now = Instant.now().toString()
        return try {
            syncStateDao.updateUploadAt(now)
            true
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to update lastUploadAt in SyncState", e)
            false
        }
    }

    /**
     * NET-009: Trim the batch so the estimated serialized JSON payload stays under
     * [MAX_ESTIMATED_PAYLOAD_BYTES]. Always returns at least one record (the first)
     * so progress is never completely stalled.
     */
    private fun trimBatchByEstimatedSize(batch: List<BufferedTransaction>): List<BufferedTransaction> {
        val includeRawPayload = configManager.config.value?.buffer?.persistRawPayloads ?: false
        var cumulative = 0L
        val trimmed = mutableListOf<BufferedTransaction>()
        for (tx in batch) {
            val recordSize = ESTIMATED_BASE_RECORD_BYTES +
                (if (includeRawPayload) tx.rawPayloadJson?.length ?: 0 else 0)
            if (trimmed.isNotEmpty() && cumulative + recordSize > MAX_ESTIMATED_PAYLOAD_BYTES) {
                AppLogger.i(
                    TAG,
                    "NET-009: Proactively trimmed batch from ${batch.size} to ${trimmed.size} records " +
                        "(estimated ${cumulative / 1024}KB, limit ${MAX_ESTIMATED_PAYLOAD_BYTES / 1024}KB)",
                )
                break
            }
            cumulative += recordSize
            trimmed.add(tx)
        }
        return trimmed
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

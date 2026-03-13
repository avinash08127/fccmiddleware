package com.fccmiddleware.edge.ingestion

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import com.fccmiddleware.edge.adapter.common.CanonicalTransaction
import com.fccmiddleware.edge.adapter.common.FccVendor
import com.fccmiddleware.edge.adapter.common.FetchCursor
import com.fccmiddleware.edge.adapter.common.FiscalizationContext
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.adapter.common.IngestionSource
import com.fccmiddleware.edge.adapter.common.IFiscalizationService
import com.fccmiddleware.edge.adapter.common.IngestionMode
import com.fccmiddleware.edge.adapter.common.TransactionStatus
import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.buffer.entity.SyncState
import com.fccmiddleware.edge.config.FiscalizationDto
import android.os.SystemClock
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.time.Instant

/**
 * Summary of a completed poll cycle — returned by [pollNow] for the manual pull API.
 */
data class PollResult(
    /** Transactions newly inserted into the local buffer. */
    val newCount: Int,
    /** Transactions skipped because they were already buffered (dedup). */
    val skippedCount: Int,
    /** Number of FCC fetch iterations performed in this poll cycle. */
    val fetchCycles: Int,
    /** True if the FCC cursor was advanced at least once during this cycle. */
    val cursorAdvanced: Boolean,
    /**
     * AF-006: Number of newly buffered transactions matching the requested pump number.
     * Null when no pump filter was requested (scheduled polls, or manual pull without pumpNumber).
     */
    val pumpMatchCount: Int? = null,
)

/**
 * IngestionOrchestrator — coordinates FCC polling, normalization, and local buffering.
 *
 * Supports ingestion modes from site config:
 *   - CLOUD_DIRECT: FCC pushes directly to cloud; agent is safety-net LAN poller.
 *     Polls on a longer interval gated by [CLOUD_DIRECT_MIN_POLL_INTERVAL_MS].
 *   - RELAY: Agent is primary receiver; polls FCC on every cadence tick, buffers, uploads.
 *   - BUFFER_ALWAYS: Same as RELAY with explicit local-first semantics.
 *
 * Offline-first guarantee: every polled transaction is buffered locally before
 * any upload attempt. No transaction is lost on connectivity failure.
 *
 * Poll scheduling is driven by [CadenceController] — this class does NOT start its
 * own timer loop. [poll] is called by the cadence loop; CLOUD_DIRECT mode adds
 * wall-clock time-gating inside [poll] to achieve the longer safety-net interval
 * without introducing a competing independent timer.
 *
 * Cursor storage convention in [SyncState.lastFccCursor]:
 *   - "token:<value>" — vendor opaque continuation token (preferred when available)
 *   - ISO 8601 UTC string — high-watermark timestamp cursor
 *   - null — first boot; full catch-up required
 */
class IngestionOrchestrator(
    /** Nullable until TransactionBufferManager is wired. Polls are no-ops until set. */
    private val bufferManager: TransactionBufferManager? = null,
    /** Nullable until Room DB is available. Polls are no-ops until set. */
    private val syncStateDao: SyncStateDao? = null,
    /** Nullable until Room DB is available. Used for fiscal receipt updates (ADV-7.3). */
    private val transactionDao: TransactionBufferDao? = null,
) {
    /** Late-bound via [wireRuntime]: wired when FCC config becomes available after startup. */
    @Volatile
    private var adapter: IFccAdapter? = null

    /** Late-bound via [wireRuntime]: wired when FCC config becomes available after startup. */
    @Volatile
    private var config: AgentFccConfig? = null

    // ── Fiscalization (ADV-7.3) ───────────────────────────────────────────────
    // Late-bound: wired when site config indicates fiscalizationMode = FCC_DIRECT + vendor = ADVATEC.

    @Volatile
    internal var fiscalizationService: IFiscalizationService? = null

    @Volatile
    internal var fiscalizationConfig: FiscalizationDto? = null

    internal fun wireRuntime(adapter: IFccAdapter?, config: AgentFccConfig?) {
        this.adapter = adapter
        this.config = config
    }

    /**
     * Wires the fiscalization service and config (ADV-7.3).
     * Called by the runtime when site config indicates post-dispense fiscalization is needed.
     */
    internal fun wireFiscalization(service: IFiscalizationService?, fiscConfig: FiscalizationDto?) {
        this.fiscalizationService = service
        this.fiscalizationConfig = fiscConfig
    }

    companion object {
        private const val TAG = "IngestionOrchestrator"

        /** Prefix used to distinguish vendor cursor tokens from timestamp cursors. */
        private const val CURSOR_TOKEN_PREFIX = "token:"

        /** Default fetch batch size per [IFccAdapter.fetchTransactions] call. */
        private const val FETCH_BATCH_SIZE = 50

        /**
         * Maximum fetch iterations per [poll] or [pollNow] to bound CPU and wall time.
         * Prevents a single poll cycle monopolizing the foreground service thread when
         * the FCC has a deep backlog.
         */
        private const val MAX_FETCH_CYCLES = 10

        /** Maximum fiscalization attempts before dead-lettering. */
        private const val MAX_FISCAL_ATTEMPTS = 5

        /** Base backoff delay for fiscalization retry in milliseconds. */
        private const val FISCAL_BASE_BACKOFF_MS = 30_000L

        /** Maximum backoff delay for fiscalization retry in milliseconds. */
        private const val FISCAL_MAX_BACKOFF_MS = 480_000L

        /**
         * Minimum wall-clock interval between CLOUD_DIRECT scheduled polls.
         *
         * CLOUD_DIRECT is a safety-net poller; FCC pushes directly to cloud in this
         * mode so the agent only needs to catch transactions the cloud missed.
         * 5 minutes is the default specified in the implementation plan.
         * Exposed as a constant so tests can override via a constructor parameter.
         */
        const val CLOUD_DIRECT_MIN_POLL_INTERVAL_MS: Long = 5 * 60 * 1_000L
    }

    /**
     * Monotonic elapsed-realtime millis of the last completed scheduled poll.
     * Uses [SystemClock.elapsedRealtime] so system clock adjustments (NTP, user,
     * daylight-saving) cannot make the elapsed interval negative or skip polls.
     * Access is serialized by [pollMutex].
     */
    internal var lastScheduledPollElapsedMs: Long? = null

    /**
     * Mutex that serializes [poll] and [pollNow] so manual and scheduled polls
     * never execute concurrently and cannot corrupt the cursor state.
     */
    private val pollMutex = Mutex()

    // -------------------------------------------------------------------------
    // Public API — called by CadenceController
    // -------------------------------------------------------------------------

    /**
     * Poll FCC for new transactions, normalize (adapter owns this), and buffer locally.
     *
     * Invoked by [CadenceController] when FCC is reachable (FULLY_ONLINE or INTERNET_DOWN).
     *
     * In CLOUD_DIRECT mode the actual FCC call is further gated by
     * [CLOUD_DIRECT_MIN_POLL_INTERVAL_MS] so the 30-second cadence tick frequency
     * does not produce excess safety-net polls.
     *
     * In RELAY and BUFFER_ALWAYS modes every cadence tick triggers a real FCC fetch;
     * the cadence interval (configured base 30 s) matches [AgentFccConfig.pullIntervalSeconds].
     */
    suspend fun poll() {
        val fccAdapter = adapter ?: run {
            AppLogger.d(TAG, "poll() skipped — adapter not wired")
            return
        }
        val fccConfig = config ?: run {
            AppLogger.d(TAG, "poll() skipped — config not available")
            return
        }

        pollMutex.withLock {
            if (fccConfig.ingestionMode == IngestionMode.CLOUD_DIRECT) {
                val last = lastScheduledPollElapsedMs
                if (last != null) {
                    val nowMonotonic = SystemClock.elapsedRealtime()
                    val elapsedMs = nowMonotonic - last
                    if (elapsedMs < CLOUD_DIRECT_MIN_POLL_INTERVAL_MS) {
                        AppLogger.d(
                            TAG,
                            "CLOUD_DIRECT: skipping scheduled poll " +
                                "(${elapsedMs}ms elapsed, min=${CLOUD_DIRECT_MIN_POLL_INTERVAL_MS}ms)",
                        )
                        return
                    }
                }
            }

            doPoll(fccAdapter, fccConfig, pumpNumber = null, isManual = false)
            lastScheduledPollElapsedMs = SystemClock.elapsedRealtime()
        }
    }

    /**
     * Trigger an immediate FCC pull, bypassing the CLOUD_DIRECT interval gate.
     *
     * Called by Odoo POS (via POST /api/v1/transactions/pull) or the diagnostic UI
     * to surface a just-completed dispense without waiting for the next scheduled poll.
     * This is a core requirement — treated as a first-class feature.
     *
     * The [pumpNumber] parameter is informational (logged for diagnostics). The
     * adapter's [IFccAdapter.fetchTransactions] returns all transactions since the
     * last cursor; pump-specific filtering is applied at the local API layer, not here.
     * All returned transactions are buffered so no data is lost for other pumps.
     *
     * Serialized with [poll] via [pollMutex] — waits for any in-progress scheduled poll
     * to complete before executing. This prevents cursor corruption and guarantees that
     * the manual pull starts from the most recently advanced cursor.
     *
     * @return [PollResult] summarising the pull, or null if the adapter or config is not
     *   yet wired (development/boot stub state).
     */
    suspend fun pollNow(pumpNumber: Int? = null): PollResult? {
        val fccAdapter = adapter ?: run {
            AppLogger.d(TAG, "pollNow(pumpNumber=$pumpNumber) skipped — adapter not wired")
            return null
        }
        val fccConfig = config ?: run {
            AppLogger.d(TAG, "pollNow(pumpNumber=$pumpNumber) skipped — config not available")
            return null
        }
        AppLogger.i(TAG, "Manual FCC pull triggered (pumpNumber=$pumpNumber, mode=${fccConfig.ingestionMode})")
        return pollMutex.withLock {
            doPoll(fccAdapter, fccConfig, pumpNumber, isManual = true)
        }
    }

    // -------------------------------------------------------------------------
    // Poll pipeline — fetch → buffer (adapter owns normalization)
    // -------------------------------------------------------------------------

    private suspend fun doPoll(
        fccAdapter: IFccAdapter,
        fccConfig: AgentFccConfig,
        pumpNumber: Int?,
        isManual: Boolean,
    ): PollResult {
        val dao = syncStateDao ?: return PollResult(0, 0, 0, false)
        val bm = bufferManager ?: return PollResult(0, 0, 0, false)

        val initialSyncState = try {
            dao.get()
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to read SyncState; aborting poll", e)
            return PollResult(0, 0, 0, false)
        }

        var cursor = buildCursor(initialSyncState)
        var fetchCycles = 0
        var newCount = 0
        var skippedCount = 0
        var pumpMatchCount = 0
        var lastBatchHasMore = false
        var cursorAdvanced = false

        // ADV-7.3: Collect newly buffered transactions for post-dispense fiscalization.
        val fiscService = fiscalizationService
        val fiscConfig = fiscalizationConfig
        val shouldFiscalize = fiscService != null
            && fiscConfig != null
            && fiscConfig.mode.equals("FCC_DIRECT", ignoreCase = true)
            && fiscConfig.vendor.equals("ADVATEC", ignoreCase = true)
        val txsToFiscalize = if (shouldFiscalize) mutableListOf<CanonicalTransaction>() else null

        try {
            do {
                AppLogger.d(
                    TAG,
                    "Fetching from FCC: mode=${fccConfig.ingestionMode} " +
                        "cycle=${fetchCycles + 1} manual=$isManual cursor=$cursor",
                )

                val batch = fccAdapter.fetchTransactions(cursor)

                for (tx in batch.transactions) {
                    val inserted = bm.bufferTransaction(tx)
                    if (inserted) {
                        newCount++
                        // AF-006: Count newly buffered transactions matching the requested pump
                        if (pumpNumber != null && tx.pumpNumber == pumpNumber) {
                            pumpMatchCount++
                        }
                        // ADV-7.3: Queue for fiscalization if no fiscal receipt yet.
                        if (txsToFiscalize != null && tx.fiscalReceiptNumber.isNullOrEmpty()) {
                            txsToFiscalize.add(tx)
                        }
                    } else {
                        skippedCount++
                    }
                }

                // Advance the persisted cursor after each successful batch
                val newCursorValue = when {
                    batch.nextCursorToken != null -> "$CURSOR_TOKEN_PREFIX${batch.nextCursorToken}"
                    batch.highWatermarkUtc != null -> batch.highWatermarkUtc
                    else -> null
                }
                if (newCursorValue != null) {
                    try {
                        advanceCursor(dao, newCursorValue)
                        cursorAdvanced = true

                        // Build next cursor for continuation fetch within this poll cycle.
                        // CRITICAL: only advance the in-memory cursor AFTER the DB persist
                        // succeeds. If advanceCursor() threw, we must NOT move past this
                        // batch — next poll will re-fetch from the old cursor, and local
                        // dedup ensures no duplicates.
                        cursor = FetchCursor(
                            cursorToken = batch.nextCursorToken,
                            sinceUtc = if (batch.nextCursorToken == null) batch.highWatermarkUtc else null,
                            limit = FETCH_BATCH_SIZE,
                        )
                    } catch (e: Exception) {
                        // Non-fatal: cursor will not advance; next poll may re-fetch the same batch.
                        // Cloud and local dedup ensure no duplicates are stored.
                        // Break out of the fetch loop since we can't safely advance.
                        AppLogger.e(TAG, "Failed to persist cursor; stopping poll cycle to prevent data loss", e)
                        break
                    }
                }

                lastBatchHasMore = batch.hasMore
                fetchCycles++
            } while (lastBatchHasMore && fetchCycles < MAX_FETCH_CYCLES)

            if (lastBatchHasMore && fetchCycles >= MAX_FETCH_CYCLES) {
                AppLogger.w(
                    TAG,
                    "MAX_FETCH_CYCLES ($MAX_FETCH_CYCLES) reached; remaining transactions " +
                        "deferred to next poll cycle",
                )
            }

            AppLogger.i(
                TAG,
                "Poll complete: mode=${fccConfig.ingestionMode} manual=$isManual " +
                    "fetchCycles=$fetchCycles new=$newCount skipped=$skippedCount",
            )
        } catch (e: Exception) {
            AppLogger.e(TAG, "FCC poll failed after $fetchCycles cycle(s)", e)
        }

        // ── ADV-7.3 / GAP-7: Mark newly buffered transactions for fiscalization ─
        if (!txsToFiscalize.isNullOrEmpty()) {
            val txDao = transactionDao
            val now = Instant.now().toString()
            for (tx in txsToFiscalize) {
                try {
                    txDao?.markFiscalPending(tx.id, now)
                } catch (e: Exception) {
                    AppLogger.w(TAG, "Failed to mark fiscal pending for tx ${tx.id}: ${e.message}")
                }
            }
            // Attempt immediate fiscalization for newly queued transactions
            retryPendingFiscalization()
        }

        return PollResult(
            newCount = newCount,
            skippedCount = skippedCount,
            fetchCycles = fetchCycles,
            cursorAdvanced = cursorAdvanced,
            pumpMatchCount = if (pumpNumber != null) pumpMatchCount else null,
        )
    }

    // -------------------------------------------------------------------------
    // Fiscalization retry with exponential backoff (GAP-7)
    // -------------------------------------------------------------------------

    /**
     * Retries pending fiscalization for transactions that have not exceeded max attempts.
     * Uses exponential backoff: 30s, 60s, 120s, 240s, 480s.
     *
     * AP-004: Called on a separate cadence timer (every Nth tick via CadenceController)
     * and immediately after new transactions are queued in [doPoll]. The separate timer
     * avoids blocking the main cadence tick with sequential Advatec HTTP calls.
     */
    internal suspend fun retryPendingFiscalization() {
        val fiscService = fiscalizationService ?: return
        val txDao = transactionDao ?: return

        // Calculate the backoff threshold — only retry records whose last attempt
        // is older than the minimum backoff (30s). The per-record backoff is checked
        // after retrieval based on individual attempt counts.
        val backoffThreshold = Instant.now()
            .minusMillis(FISCAL_BASE_BACKOFF_MS)
            .toString()

        val pending = try {
            txDao.getPendingFiscalization(MAX_FISCAL_ATTEMPTS, backoffThreshold, 10)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to query pending fiscalization", e)
            return
        }

        if (pending.isEmpty()) return

        AppLogger.i(TAG, "Fiscalization retry: ${pending.size} transaction(s) pending")

        for (tx in pending) {
            // Per-record exponential backoff check
            val requiredBackoffMs = (FISCAL_BASE_BACKOFF_MS * (1L shl tx.fiscalAttempts.coerceAtMost(10)))
                .coerceAtMost(FISCAL_MAX_BACKOFF_MS)
            if (tx.lastFiscalAttemptAt != null) {
                try {
                    val lastAttempt = Instant.parse(tx.lastFiscalAttemptAt)
                    val elapsedMs = System.currentTimeMillis() - lastAttempt.toEpochMilli()
                    if (elapsedMs < requiredBackoffMs) continue
                } catch (_: Exception) { /* parse failure — proceed with retry */ }
            }

            val now = Instant.now().toString()

            try {
                val context = FiscalizationContext(
                    customerTaxId = tx.fiscalCustomerTaxId,
                    customerName = tx.fiscalCustomerName,
                    customerIdType = tx.fiscalCustomerIdType,
                    paymentType = tx.paymentType,
                )
                val result = fiscService.submitForFiscalization(
                    // Reconstruct a minimal CanonicalTransaction from buffered fields
                    CanonicalTransaction(
                        id = tx.id,
                        fccTransactionId = tx.fccTransactionId,
                        siteCode = tx.siteCode,
                        pumpNumber = tx.pumpNumber,
                        nozzleNumber = tx.nozzleNumber,
                        productCode = tx.productCode,
                        volumeMicrolitres = tx.volumeMicrolitres,
                        amountMinorUnits = tx.amountMinorUnits,
                        unitPriceMinorPerLitre = tx.unitPriceMinorPerLitre,
                        currencyCode = tx.currencyCode,
                        startedAt = tx.startedAt,
                        completedAt = tx.completedAt,
                        fccVendor = FccVendor.valueOf(tx.fccVendor),
                        legalEntityId = "",  // Not stored in buffer; populated upstream on cloud upload
                        status = TransactionStatus.valueOf(tx.status),
                        ingestionSource = IngestionSource.valueOf(tx.ingestionSource),
                        ingestedAt = tx.createdAt,
                        updatedAt = tx.updatedAt,
                        schemaVersion = tx.schemaVersion,
                        isDuplicate = false,
                        correlationId = tx.correlationId,
                        rawPayloadJson = tx.rawPayloadJson,
                    ),
                    context,
                )

                if (result.success && !result.receiptCode.isNullOrEmpty()) {
                    txDao.markFiscalSuccess(tx.id, result.receiptCode, now)
                    AppLogger.i(TAG, "Fiscalization retry: receipt attached to tx ${tx.id} — attempt=${tx.fiscalAttempts + 1}")
                } else {
                    if (tx.fiscalAttempts + 1 >= MAX_FISCAL_ATTEMPTS) {
                        txDao.markFiscalDeadLetter(tx.id, now)
                        AppLogger.e(TAG, "Fiscalization dead-letter: tx ${tx.id} exceeded $MAX_FISCAL_ATTEMPTS attempts — ${result.errorMessage}")
                    } else {
                        txDao.recordFiscalFailure(tx.id, now)
                        AppLogger.w(TAG, "Fiscalization retry failed: tx ${tx.id} attempt=${tx.fiscalAttempts + 1} — ${result.errorMessage}")
                    }
                }
            } catch (e: kotlin.coroutines.cancellation.CancellationException) {
                throw e
            } catch (e: Exception) {
                if (tx.fiscalAttempts + 1 >= MAX_FISCAL_ATTEMPTS) {
                    txDao.markFiscalDeadLetter(tx.id, now)
                    AppLogger.e(TAG, "Fiscalization dead-letter: tx ${tx.id} exceeded $MAX_FISCAL_ATTEMPTS attempts — ${e.message}")
                } else {
                    txDao.recordFiscalFailure(tx.id, now)
                    AppLogger.w(TAG, "Fiscalization retry error: tx ${tx.id} attempt=${tx.fiscalAttempts + 1} — ${e.message}")
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Cursor helpers
    // -------------------------------------------------------------------------

    /**
     * Build a [FetchCursor] from the persisted [SyncState.lastFccCursor].
     *
     * Convention:
     *   - null → first boot; full catch-up (empty cursor)
     *   - "token:<value>" → vendor opaque continuation token
     *   - any other string → ISO 8601 UTC high-watermark
     */
    internal fun buildCursor(syncState: SyncState?): FetchCursor {
        val stored = syncState?.lastFccCursor
        return when {
            stored == null -> {
                AppLogger.i(TAG, "No prior cursor — starting full catch-up")
                FetchCursor(limit = FETCH_BATCH_SIZE)
            }
            stored.startsWith(CURSOR_TOKEN_PREFIX) -> FetchCursor(
                cursorToken = stored.removePrefix(CURSOR_TOKEN_PREFIX),
                limit = FETCH_BATCH_SIZE,
            )
            else -> FetchCursor(
                sinceUtc = stored,
                limit = FETCH_BATCH_SIZE,
            )
        }
    }

    /**
     * Persist the advanced cursor to [SyncState] (id = 1).
     *
     * AF-003: Re-reads the current SyncState from the DAO instead of using a
     * stale snapshot. This prevents other fields (e.g., lastUploadAt set by
     * CloudUploadWorker) from being silently rolled back during multi-batch
     * poll cycles.
     */
    private suspend fun advanceCursor(
        dao: SyncStateDao,
        newCursorValue: String,
    ) {
        val now = Instant.now().toString()
        val current = dao.get()
        val updated = current?.copy(
            lastFccCursor = newCursorValue,
            updatedAt = now,
        ) ?: SyncState(
            lastFccCursor = newCursorValue,
            lastUploadAt = null,
            lastStatusPollAt = null,
            lastConfigPullAt = null,
            lastConfigVersion = null,
            updatedAt = now,
        )
        dao.upsert(updated)
    }
}

package com.fccmiddleware.edge.ingestion

import android.util.Log
import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import com.fccmiddleware.edge.adapter.common.FetchCursor
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.adapter.common.IngestionMode
import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.entity.SyncState
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
    /** Nullable until the adapter factory is wired (EA-2.x). Polls are no-ops until set. */
    private val adapter: IFccAdapter? = null,
    /** Nullable until TransactionBufferManager is wired. Polls are no-ops until set. */
    private val bufferManager: TransactionBufferManager? = null,
    /** Nullable until Room DB is available. Polls are no-ops until set. */
    private val syncStateDao: SyncStateDao? = null,
    /** Nullable until ConfigManager is wired (EA-2.x). Polls are no-ops until set. */
    private val config: AgentFccConfig? = null,
) {
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
     * Wall-clock time of the last completed scheduled poll.
     * Used for CLOUD_DIRECT interval gating inside [poll].
     * Volatile so the CadenceController coroutine and a concurrent [pollNow] see a
     * consistent value; not a full mutex since double-polling is safe (cloud dedup handles it).
     */
    @Volatile
    internal var lastScheduledPollAt: Instant? = null

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
            Log.d(TAG, "poll() skipped — adapter not wired")
            return
        }
        val fccConfig = config ?: run {
            Log.d(TAG, "poll() skipped — config not available")
            return
        }

        if (fccConfig.ingestionMode == IngestionMode.CLOUD_DIRECT) {
            val last = lastScheduledPollAt
            if (last != null) {
                val elapsedMs = Instant.now().toEpochMilli() - last.toEpochMilli()
                if (elapsedMs < CLOUD_DIRECT_MIN_POLL_INTERVAL_MS) {
                    Log.d(
                        TAG,
                        "CLOUD_DIRECT: skipping scheduled poll " +
                            "(${elapsedMs}ms elapsed, min=${CLOUD_DIRECT_MIN_POLL_INTERVAL_MS}ms)",
                    )
                    return
                }
            }
        }

        pollMutex.withLock {
            doPoll(fccAdapter, fccConfig, pumpNumber = null, isManual = false)
        }
        lastScheduledPollAt = Instant.now()
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
            Log.d(TAG, "pollNow(pumpNumber=$pumpNumber) skipped — adapter not wired")
            return null
        }
        val fccConfig = config ?: run {
            Log.d(TAG, "pollNow(pumpNumber=$pumpNumber) skipped — config not available")
            return null
        }
        Log.i(TAG, "Manual FCC pull triggered (pumpNumber=$pumpNumber, mode=${fccConfig.ingestionMode})")
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
            Log.e(TAG, "Failed to read SyncState; aborting poll", e)
            return PollResult(0, 0, 0, false)
        }

        var cursor = buildCursor(initialSyncState)
        var fetchCycles = 0
        var newCount = 0
        var skippedCount = 0
        var lastBatchHasMore = false
        var cursorAdvanced = false

        try {
            do {
                Log.d(
                    TAG,
                    "Fetching from FCC: mode=${fccConfig.ingestionMode} " +
                        "cycle=${fetchCycles + 1} manual=$isManual cursor=$cursor",
                )

                val batch = fccAdapter.fetchTransactions(cursor)

                for (tx in batch.transactions) {
                    val inserted = bm.bufferTransaction(tx)
                    if (inserted) newCount++ else skippedCount++
                }

                // Advance the persisted cursor after each successful batch
                val newCursorValue = when {
                    batch.nextCursorToken != null -> "$CURSOR_TOKEN_PREFIX${batch.nextCursorToken}"
                    batch.highWatermarkUtc != null -> batch.highWatermarkUtc
                    else -> null
                }
                if (newCursorValue != null) {
                    try {
                        advanceCursor(dao, initialSyncState, newCursorValue)
                        cursorAdvanced = true
                    } catch (e: Exception) {
                        // Non-fatal: cursor will not advance; next poll may re-fetch the same batch.
                        // Cloud and local dedup ensure no duplicates are stored.
                        Log.e(TAG, "Failed to persist cursor; next poll may re-fetch this batch", e)
                    }

                    // Build next cursor for continuation fetch within this poll cycle
                    cursor = FetchCursor(
                        cursorToken = batch.nextCursorToken,
                        sinceUtc = if (batch.nextCursorToken == null) batch.highWatermarkUtc else null,
                        limit = FETCH_BATCH_SIZE,
                    )
                }

                lastBatchHasMore = batch.hasMore
                fetchCycles++
            } while (lastBatchHasMore && fetchCycles < MAX_FETCH_CYCLES)

            if (lastBatchHasMore && fetchCycles >= MAX_FETCH_CYCLES) {
                Log.w(
                    TAG,
                    "MAX_FETCH_CYCLES ($MAX_FETCH_CYCLES) reached; remaining transactions " +
                        "deferred to next poll cycle",
                )
            }

            Log.i(
                TAG,
                "Poll complete: mode=${fccConfig.ingestionMode} manual=$isManual " +
                    "fetchCycles=$fetchCycles new=$newCount skipped=$skippedCount",
            )
        } catch (e: Exception) {
            Log.e(TAG, "FCC poll failed after $fetchCycles cycle(s)", e)
        }

        return PollResult(newCount, skippedCount, fetchCycles, cursorAdvanced)
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
                Log.i(TAG, "No prior cursor — starting full catch-up")
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
     * Preserves all other [SyncState] fields and only updates
     * [SyncState.lastFccCursor] and [SyncState.updatedAt].
     */
    private suspend fun advanceCursor(
        dao: SyncStateDao,
        current: SyncState?,
        newCursorValue: String,
    ) {
        val now = Instant.now().toString()
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

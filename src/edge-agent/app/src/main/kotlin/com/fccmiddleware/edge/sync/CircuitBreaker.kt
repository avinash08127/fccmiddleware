package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.logging.AppLogger
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.time.Instant

/**
 * Lightweight circuit breaker for cloud workers (M-08).
 *
 * ## States
 * - **CLOSED**: Normal operation. Failures increment the counter and apply exponential backoff.
 * - **OPEN**: After [openThreshold] consecutive failures the circuit opens. All calls are
 *   rejected immediately (no HTTP request) until [resetOnConnectivityRecovery] is called
 *   or the half-open probe window expires.
 * - **HALF_OPEN**: After [halfOpenAfterMs] in OPEN, the next call is allowed through as a
 *   probe. If it succeeds the circuit closes; if it fails the circuit reopens.
 *
 * ## Thread safety
 * All mutable state is guarded by [mutex].
 *
 * ## Reset on connectivity recovery
 * [CadenceController] calls [resetOnConnectivityRecovery] when internet transitions to UP,
 * which immediately moves the circuit to CLOSED so the worker retries without waiting for
 * the half-open window.
 */
class CircuitBreaker(
    /** Human-readable label for log messages (e.g. "CloudUploadWorker"). */
    private val name: String,
    /** Base exponential backoff delay in milliseconds. */
    private val baseBackoffMs: Long = 1_000L,
    /** Maximum backoff delay (cap) in milliseconds. */
    private val maxBackoffMs: Long = 60_000L,
    /** Number of consecutive failures before the circuit opens. */
    val openThreshold: Int = 20,
    /** Time in OPEN state before allowing a half-open probe (default 5 minutes). */
    private val halfOpenAfterMs: Long = 300_000L,
) {
    enum class State { CLOSED, OPEN, HALF_OPEN }

    private val mutex = Mutex()

    @Volatile
    internal var consecutiveFailureCount: Int = 0
    @Volatile
    internal var nextRetryAt: Instant = Instant.EPOCH
    @Volatile
    internal var state: State = State.CLOSED
    private var openedAt: Instant = Instant.EPOCH

    companion object {
        private const val TAG = "CircuitBreaker"
    }

    /**
     * Check whether a call is allowed right now.
     *
     * Returns `true` if the caller should proceed; `false` if the call should be skipped.
     * In HALF_OPEN state this returns `true` once to probe, then blocks until result.
     */
    suspend fun allowRequest(): Boolean = mutex.withLock {
        val now = Instant.now()
        when (state) {
            State.CLOSED -> {
                if (now.isBefore(nextRetryAt)) {
                    return@withLock false // backoff still active
                }
                true
            }

            State.OPEN -> {
                val elapsed = now.toEpochMilli() - openedAt.toEpochMilli()
                if (elapsed >= halfOpenAfterMs) {
                    state = State.HALF_OPEN
                    AppLogger.i(TAG, "[$name] Circuit OPEN → HALF_OPEN (probing)")
                    true
                } else {
                    false
                }
            }

            State.HALF_OPEN -> true // allow the probe attempt
        }
    }

    /** Record a successful call — resets the circuit to CLOSED. */
    suspend fun recordSuccess() = mutex.withLock {
        if (state != State.CLOSED || consecutiveFailureCount > 0) {
            AppLogger.i(TAG, "[$name] Circuit → CLOSED (success after $consecutiveFailureCount failures)")
        }
        consecutiveFailureCount = 0
        nextRetryAt = Instant.EPOCH
        state = State.CLOSED
    }

    /** Record a failed call — increments backoff and may open the circuit. */
    suspend fun recordFailure(): Long = mutex.withLock {
        consecutiveFailureCount++
        val backoffMs = calculateBackoffMs(consecutiveFailureCount)
        nextRetryAt = Instant.now().plusMillis(backoffMs)

        if (consecutiveFailureCount >= openThreshold && state != State.OPEN) {
            state = State.OPEN
            openedAt = Instant.now()
            AppLogger.e(
                TAG,
                "[$name] Circuit OPEN after $consecutiveFailureCount consecutive failures. " +
                    "All requests blocked until connectivity recovery or half-open probe in ${halfOpenAfterMs / 1000}s.",
            )
        } else if (state == State.HALF_OPEN) {
            // Probe failed — reopen
            state = State.OPEN
            openedAt = Instant.now()
            AppLogger.w(TAG, "[$name] Half-open probe failed — circuit re-opened")
        }
        backoffMs
    }

    /**
     * Called by [CadenceController] when internet connectivity recovers.
     * Immediately resets the circuit to CLOSED so workers retry without waiting.
     */
    suspend fun resetOnConnectivityRecovery() = mutex.withLock {
        if (state != State.CLOSED || consecutiveFailureCount > 0) {
            AppLogger.i(
                TAG,
                "[$name] Circuit reset on connectivity recovery " +
                    "(was ${state.name}, failures=$consecutiveFailureCount)",
            )
        }
        consecutiveFailureCount = 0
        nextRetryAt = Instant.EPOCH
        state = State.CLOSED
    }

    /**
     * M-15: Set an explicit backoff duration in seconds (e.g. from a Retry-After header).
     * Does not increment the failure counter — rate limiting is flow control, not a fault.
     */
    suspend fun setBackoffSeconds(seconds: Long) = mutex.withLock {
        nextRetryAt = Instant.now().plusSeconds(seconds)
        AppLogger.i(TAG, "[$name] Explicit backoff set: ${seconds}s (no failure count increment)")
    }

    /**
     * AT-007: Atomic snapshot of circuit breaker state for consistent diagnostics logging.
     * Reads all fields under the mutex so combined state (e.g. consecutiveFailureCount + state)
     * cannot represent different moments in time (TOCTOU).
     */
    data class Snapshot(
        val state: State,
        val consecutiveFailureCount: Int,
        val nextRetryAt: Instant,
    )

    suspend fun snapshot(): Snapshot = mutex.withLock {
        Snapshot(
            state = state,
            consecutiveFailureCount = consecutiveFailureCount,
            nextRetryAt = nextRetryAt,
        )
    }

    /** Remaining backoff millis, or 0 if none. For logging/diagnostics. */
    suspend fun remainingBackoffMs(): Long = mutex.withLock {
        val now = Instant.now()
        if (now.isBefore(nextRetryAt)) {
            nextRetryAt.toEpochMilli() - now.toEpochMilli()
        } else {
            0L
        }
    }

    private fun calculateBackoffMs(failureCount: Int): Long {
        if (failureCount <= 0) return 0L
        val shift = (failureCount - 1).coerceAtMost(30)
        val exponential = baseBackoffMs * (1L shl shift)
        return minOf(exponential, maxBackoffMs)
    }
}

package com.fccmiddleware.edge.connectivity

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.dao.AuditLogDao
import com.fccmiddleware.edge.buffer.entity.AuditLog
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import java.time.Instant
import java.util.UUID
import kotlin.random.Random

/**
 * ConnectivityManager — dual-probe connectivity state machine.
 *
 * Two independent coroutines run the internet and FCC probes concurrently.
 *
 * DOWN transition: 3 consecutive probe failures required (prevents flapping).
 * UP recovery:    1 successful probe immediately transitions back to UP.
 *
 * Exposes [state] as a [StateFlow<ConnectivityState>] for observers.
 *
 * Architecture rule: probes are injectable lambdas for testability — the caller
 * wires the real HTTP and FCC adapter calls. Network binding (WiFi for FCC,
 * mobile/cloud for internet) is applied at the probe lambda level, not inside
 * this class, so unit tests can supply simple stubs without Android dependencies.
 *
 * When [networkBinder] is provided, each probe loop logs which physical network
 * it is running over (WiFi vs mobile) for diagnostics.
 */
class ConnectivityManager(
    /** suspend () -> Boolean: true = internet reachable; wraps HTTP GET /health with 5s timeout */
    private val internetProbe: suspend () -> Boolean,
    /** suspend () -> Boolean: true = FCC reachable; wraps IFccAdapter.heartbeat() with 5s timeout */
    private val fccProbe: suspend () -> Boolean,
    private val auditLogDao: AuditLogDao,
    private val scope: CoroutineScope,
    val config: ProbeConfig = ProbeConfig(),
    /** Optional — used only for logging which physical network each probe runs over. */
    private val networkBinder: NetworkBinder? = null,
) {
    data class ProbeConfig(
        val probeIntervalMs: Long = 30_000L,
        val probeTimeoutMs: Long = 5_000L,
        val failureThreshold: Int = 3,
        /** Consecutive successes required before transitioning from DOWN → UP.
         *  Prevents oscillation under marginal networks (F,F,F,S,F,S...). */
        val recoveryThreshold: Int = 2,
        val jitterRangeMs: Long = 3_000L,
    )

    private val _state = MutableStateFlow(ConnectivityState.FULLY_OFFLINE)
    val state: StateFlow<ConnectivityState> = _state.asStateFlow()

    /** Mutex protecting all mutable probe state fields below. */
    private val mutex = Mutex()

    // Probe up/down flags — only written inside mutex
    private var internetUp = false
    private var fccUp = false
    private var internetConsecFailures = 0
    private var fccConsecFailures = 0
    private var internetConsecSuccesses = 0
    private var fccConsecSuccesses = 0

    // AF-050: Skip recovery threshold on the very first probe round. The initial
    // FULLY_OFFLINE state has no prior "DOWN" evidence to protect against oscillation,
    // so requiring multiple consecutive successes just adds a ~30s cold-start delay.
    private var internetInitialProbe = true
    private var fccInitialProbe = true

    // Diagnostics timestamps (ms epoch, volatile for read outside mutex)
    @Volatile var lastInternetProbeMs: Long = 0L
    @Volatile var lastFccProbeMs: Long = 0L
    @Volatile var lastFccSuccessMs: Long = 0L

    private var probeJobs: Job? = null

    // LR-005: Guard to make stop() terminal. Once stopped, start() is a no-op
    // to prevent accidental restart on the same scope after the lifecycle has ended.
    private var stopped = false

    companion object {
        private const val TAG = "ConnectivityManager"
    }

    /**
     * Start both probe loops immediately. First probe runs without delay
     * per spec: "Initialize in FULLY_OFFLINE on app start, run both probes immediately."
     *
     * Once [stop] has been called, subsequent calls to [start] are ignored.
     */
    fun start() {
        if (stopped) {
            AppLogger.w(TAG, "start() called after stop() — ignoring (create a new instance to restart)")
            return
        }
        probeJobs?.cancel()
        probeJobs = scope.launch {
            launch { runInternetProbeLoop() }
            launch { runFccProbeLoop() }
        }
        AppLogger.i(TAG, "ConnectivityManager started (initial state = FULLY_OFFLINE)")
    }

    fun stop() {
        stopped = true
        probeJobs?.cancel()
        // AT-049: Reset all state so observers see FULLY_OFFLINE immediately after stop,
        // and no stale counters survive if the instance is ever re-used.
        _state.value = ConnectivityState.FULLY_OFFLINE
        internetUp = false
        fccUp = false
        internetConsecFailures = 0
        fccConsecFailures = 0
        internetConsecSuccesses = 0
        fccConsecSuccesses = 0
        AppLogger.i(TAG, "ConnectivityManager stopped")
    }

    // -------------------------------------------------------------------------
    // Internet probe loop
    // -------------------------------------------------------------------------

    private suspend fun runInternetProbeLoop() {
        while (currentCoroutineContext().isActive) {
            logProbeNetwork(isInternet = true)
            val success = runProbeWithTimeout { internetProbe() }
            lastInternetProbeMs = System.currentTimeMillis()
            processProbeResult(isInternet = true, success = success)
            delay(config.probeIntervalMs + jitter())
        }
    }

    // -------------------------------------------------------------------------
    // FCC probe loop
    // -------------------------------------------------------------------------

    private suspend fun runFccProbeLoop() {
        while (currentCoroutineContext().isActive) {
            logProbeNetwork(isInternet = false)
            val success = runProbeWithTimeout { fccProbe() }
            lastFccProbeMs = System.currentTimeMillis()
            if (success) lastFccSuccessMs = System.currentTimeMillis()
            processProbeResult(isInternet = false, success = success)
            delay(config.probeIntervalMs + jitter())
        }
    }

    // -------------------------------------------------------------------------
    // Probe result processing
    // -------------------------------------------------------------------------

    /**
     * AP-027: State derivation is mutex-protected; audit log write and listener
     * notification happen outside the lock to avoid blocking the other probe loop
     * on disk I/O.
     */
    private suspend fun processProbeResult(isInternet: Boolean, success: Boolean) {
        val transition: Pair<ConnectivityState, ConnectivityState>? = mutex.withLock {
            val prevInternetUp = internetUp
            val prevFccUp = fccUp

            if (isInternet) {
                if (success) {
                    internetConsecFailures = 0
                    internetConsecSuccesses++
                    // AF-050: On the initial probe, skip recovery threshold to avoid
                    // ~30s cold-start delay. The initial FULLY_OFFLINE state has no
                    // prior DOWN evidence, so anti-oscillation is not needed.
                    if (internetInitialProbe || internetConsecSuccesses >= config.recoveryThreshold) {
                        internetUp = true
                        internetInitialProbe = false
                    }
                } else {
                    internetConsecSuccesses = 0
                    internetConsecFailures++
                    internetInitialProbe = false
                    if (internetConsecFailures >= config.failureThreshold) {
                        internetUp = false
                    }
                }
            } else {
                if (success) {
                    fccConsecFailures = 0
                    fccConsecSuccesses++
                    if (fccInitialProbe || fccConsecSuccesses >= config.recoveryThreshold) {
                        fccUp = true
                        fccInitialProbe = false
                    }
                } else {
                    fccConsecSuccesses = 0
                    fccConsecFailures++
                    fccInitialProbe = false
                    if (fccConsecFailures >= config.failureThreshold) {
                        fccUp = false
                    }
                }
            }

            val probeChanged = internetUp != prevInternetUp || fccUp != prevFccUp
            if (probeChanged) {
                val newState = when {
                    internetUp && fccUp -> ConnectivityState.FULLY_ONLINE
                    !internetUp && fccUp -> ConnectivityState.INTERNET_DOWN
                    internetUp && !fccUp -> ConnectivityState.FCC_UNREACHABLE
                    else -> ConnectivityState.FULLY_OFFLINE
                }
                val prevState = _state.value
                if (newState != prevState) {
                    _state.value = newState
                    Pair(prevState, newState)
                } else null
            } else null
        }

        // Audit log write and listener notification outside the mutex
        if (transition != null) {
            val (prevState, newState) = transition
            AppLogger.i(TAG, "State transition: $prevState → $newState")
            val now = Instant.now().toString()
            try {
                auditLogDao.insert(
                    AuditLog(
                        eventType = "CONNECTIVITY_TRANSITION",
                        message = "$prevState → $newState",
                        correlationId = UUID.randomUUID().toString(),
                        createdAt = now,
                    )
                )
            } catch (e: Exception) {
                AppLogger.w(TAG, "Failed to write connectivity audit log: ${e.message}")
            }
        }
    }

    // -------------------------------------------------------------------------
    // Diagnostics
    // -------------------------------------------------------------------------

    /**
     * Seconds since the last successful FCC heartbeat probe.
     * Returns null if no successful probe has occurred since start.
     */
    fun fccHeartbeatAgeSeconds(): Int? {
        val ms = lastFccSuccessMs
        if (ms == 0L) return null
        return ((System.currentTimeMillis() - ms) / 1_000L).toInt()
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /**
     * Log which physical network a probe will use. Debug-level to avoid log spam
     * on every 30s tick; promoted to info on first probe or when the network changes.
     */
    private var lastInternetNetworkId: String? = null
    private var lastFccNetworkId: String? = null

    private fun logProbeNetwork(isInternet: Boolean) {
        val binder = networkBinder ?: return
        if (isInternet) {
            val network = binder.cloudNetwork.value
            val id = network?.toString() ?: "default-routing"
            val source = when {
                network == null -> "no bound network"
                network == binder.mobileNetwork.value -> "mobile"
                network == binder.wifiNetwork.value -> "WiFi (mobile unavailable)"
                else -> "unknown"
            }
            if (id != lastInternetNetworkId) {
                AppLogger.i(TAG, "Internet probe network: $source [$id]")
                lastInternetNetworkId = id
            }
        } else {
            val network = binder.wifiNetwork.value
            val id = network?.toString() ?: "default-routing"
            if (id != lastFccNetworkId) {
                AppLogger.i(TAG, "FCC probe network: ${if (network != null) "WiFi" else "no WiFi"} [$id]")
                lastFccNetworkId = id
            }
        }
    }

    private suspend fun runProbeWithTimeout(probe: suspend () -> Boolean): Boolean {
        return try {
            withTimeoutOrNull(config.probeTimeoutMs) { probe() } ?: false
        } catch (e: Exception) {
            AppLogger.d(TAG, "Probe exception: ${e.javaClass.simpleName}: ${e.message}")
            false
        }
    }

    private fun jitter(): Long = if (config.jitterRangeMs <= 0L) 0L else Random.nextLong(0L, config.jitterRangeMs)
}

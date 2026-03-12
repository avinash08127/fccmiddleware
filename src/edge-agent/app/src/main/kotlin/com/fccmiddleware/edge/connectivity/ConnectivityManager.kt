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
 * Notifies [ConnectivityTransitionListener] for worker side-effect triggers.
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
    private val listener: ConnectivityTransitionListener? = null,
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

    // Diagnostics timestamps (ms epoch, volatile for read outside mutex)
    @Volatile var lastInternetProbeMs: Long = 0L
    @Volatile var lastFccProbeMs: Long = 0L
    @Volatile var lastFccSuccessMs: Long = 0L

    private var probeJobs: Job? = null

    companion object {
        private const val TAG = "ConnectivityManager"
    }

    /**
     * Start both probe loops immediately. First probe runs without delay
     * per spec: "Initialize in FULLY_OFFLINE on app start, run both probes immediately."
     */
    fun start() {
        probeJobs?.cancel()
        probeJobs = scope.launch {
            launch { runInternetProbeLoop() }
            launch { runFccProbeLoop() }
        }
        AppLogger.i(TAG, "ConnectivityManager started (initial state = FULLY_OFFLINE)")
    }

    fun stop() {
        probeJobs?.cancel()
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
    // Probe result processing (mutex-protected)
    // -------------------------------------------------------------------------

    private suspend fun processProbeResult(isInternet: Boolean, success: Boolean) {
        mutex.withLock {
            val prevInternetUp = internetUp
            val prevFccUp = fccUp

            if (isInternet) {
                if (success) {
                    internetConsecFailures = 0
                    internetConsecSuccesses++
                    // Require recoveryThreshold consecutive successes before UP recovery
                    // (prevents oscillation under marginal networks)
                    if (internetConsecSuccesses >= config.recoveryThreshold) {
                        internetUp = true
                    }
                } else {
                    internetConsecSuccesses = 0
                    internetConsecFailures++
                    if (internetConsecFailures >= config.failureThreshold) {
                        internetUp = false
                    }
                }
            } else {
                if (success) {
                    fccConsecFailures = 0
                    fccConsecSuccesses++
                    if (fccConsecSuccesses >= config.recoveryThreshold) {
                        fccUp = true
                    }
                } else {
                    fccConsecSuccesses = 0
                    fccConsecFailures++
                    if (fccConsecFailures >= config.failureThreshold) {
                        fccUp = false
                    }
                }
            }

            val probeChanged = internetUp != prevInternetUp || fccUp != prevFccUp
            if (probeChanged) {
                deriveAndEmitStateUnlocked()
            }
        }
    }

    // -------------------------------------------------------------------------
    // State derivation (call with mutex held)
    // -------------------------------------------------------------------------

    private suspend fun deriveAndEmitStateUnlocked() {
        val newState = when {
            internetUp && fccUp -> ConnectivityState.FULLY_ONLINE
            !internetUp && fccUp -> ConnectivityState.INTERNET_DOWN
            internetUp && !fccUp -> ConnectivityState.FCC_UNREACHABLE
            else -> ConnectivityState.FULLY_OFFLINE
        }
        val prevState = _state.value
        if (newState == prevState) return

        _state.value = newState
        AppLogger.i(TAG, "State transition: $prevState → $newState")

        // Write audit log — suspend OK here (called from probe coroutine, not UI)
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

        listener?.onTransition(prevState, newState)
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

/**
 * Listener for connectivity state transition side effects.
 *
 * Implemented by CadenceController (or foreground service) to react to state
 * changes with worker start/stop logic per §5.4 transition table.
 *
 * Declared as `fun interface` to enable SAM conversion (lambda syntax) in tests.
 */
fun interface ConnectivityTransitionListener {
    fun onTransition(from: ConnectivityState, to: ConnectivityState)
}

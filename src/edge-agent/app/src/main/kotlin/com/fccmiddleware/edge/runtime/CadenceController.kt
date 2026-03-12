package com.fccmiddleware.edge.runtime

import android.util.Log
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.adapter.common.IFccConnectionLifecycle
import com.fccmiddleware.edge.adapter.common.IFccEventListener
import com.fccmiddleware.edge.adapter.common.PumpState
import com.fccmiddleware.edge.adapter.common.TransactionNotification
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.preauth.PreAuthHandler
import com.fccmiddleware.edge.sync.CloudUploadWorker
import com.fccmiddleware.edge.sync.ConfigPollWorker
import com.fccmiddleware.edge.sync.PreAuthCloudForwardWorker
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlin.random.Random

/**
 * CadenceController — single coalesced cadence loop for all resident periodic work.
 *
 * Architecture rules enforced here:
 *   - ONE orchestrator loop owns ALL recurring background work.
 *   - FCC polling, cloud upload, SYNCED_TO_ODOO status poll, telemetry, and config
 *     refresh are all coalesced under this controller. No independent timer loops.
 *   - Jitter applied to each interval to prevent synchronized bursts across devices.
 *   - Cadence adapts by connectivity state (back off when FULLY_OFFLINE) and
 *     backlog depth (speed up when buffer is deep).
 *   - Non-critical work (telemetry, config refresh) piggybacks on existing
 *     successful cycles; never left permanently hot.
 *
 * Side effects on connectivity transitions (per §5.4):
 *   - FULLY_ONLINE: trigger immediate buffer replay + SYNCED_TO_ODOO status sync.
 *   - Other transitions: workers are naturally suspended on next tick via state check.
 *
 * M-10: Observes [ConnectivityManager.state] StateFlow only — does NOT implement
 * [ConnectivityTransitionListener]. The listener callback was redundant with the
 * StateFlow collection and caused double-trigger on recovery transitions.
 */
class CadenceController(
    private val connectivityManager: ConnectivityManager,
    private val ingestionOrchestrator: IngestionOrchestrator,
    private val cloudUploadWorker: CloudUploadWorker,
    private val transactionDao: TransactionBufferDao,
    private val scope: CoroutineScope,
    /** Nullable until PreAuthHandler is fully wired (EA-2.5). Null disables expiry checks. */
    private val preAuthHandler: PreAuthHandler? = null,
    /** Config poll worker — polls cloud for config updates. Nullable until EA-3.3 wired. */
    private val configPollWorker: ConfigPollWorker? = null,
    /** Pre-auth cloud forward worker — forwards unsynced pre-auth records to cloud. */
    private val preAuthCloudForwardWorker: PreAuthCloudForwardWorker? = null,
    /** Token provider — checked between worker calls to short-circuit on decommission. */
    private val tokenProvider: com.fccmiddleware.edge.sync.DeviceTokenProvider? = null,
    /** FCC adapter — used for lifecycle management if it implements IFccConnectionLifecycle. */
    private val fccAdapter: IFccAdapter? = null,
    val config: CadenceConfig = CadenceConfig(),
) {

    data class CadenceConfig(
        /** Base tick interval during normal operation. */
        val baseIntervalMs: Long = 30_000L,
        /** Jitter range added to each interval to avoid device synchronisation. */
        val jitterRangeMs: Long = 5_000L,
        /** Shorter interval when buffer backlog exceeds [highBacklogThreshold]. */
        val highBacklogIntervalMs: Long = 10_000L,
        val highBacklogThreshold: Int = 500,
        /** Longer interval when fully offline (both probes down). */
        val offlineIntervalMs: Long = 60_000L,
        /** Run SYNCED_TO_ODOO poll every N ticks to share the cloud health cycle. */
        val syncedToOdooTickFrequency: Int = 2,
        /** Run config poll every N ticks. */
        val configPollTickFrequency: Int = 6,
        /** Run telemetry report every N ticks. */
        val telemetryTickFrequency: Int = 4,
    )

    private var cadenceJob: Job? = null
    private var tickCount = 0L

    /**
     * Resolved at construction: non-null only when the adapter supports persistent connections.
     * Used to call connect/disconnect and wire event listener callbacks.
     */
    private val connectionLifecycle: IFccConnectionLifecycle? =
        fccAdapter as? IFccConnectionLifecycle

    /** Event listener that bridges unsolicited FCC events into the cadence controller. */
    private val fccEventListener = object : IFccEventListener {
        override fun onPumpStatusChanged(pumpNumber: Int, newState: PumpState, fccStatusCode: String?) {
            Log.d(TAG, "FCC pump $pumpNumber status -> $newState (fccCode=$fccStatusCode)")
        }

        override fun onTransactionAvailable(notification: TransactionNotification) {
            Log.i(TAG, "FCC transaction available: fp=${notification.fpId}, idx=${notification.transactionBufferIndex}")
            triggerImmediateFccPoll()
        }

        override fun onFuellingUpdate(pumpNumber: Int, volumeMicrolitres: Long, amountMinorUnits: Long) {
            Log.d(TAG, "FCC fuelling update: pump=$pumpNumber, vol=$volumeMicrolitres µL, amt=$amountMinorUnits")
        }

        override fun onConnectionLost(reason: String) {
            Log.w(TAG, "FCC connection lost: $reason — marking unreachable, scheduling reconnect")
            scope.launch { attemptReconnect() }
        }
    }

    /**
     * L-07: Tick modulus — tickCount wraps at LCM of all tick frequencies to prevent
     * Long overflow from changing modulo behavior at the overflow boundary.
     * At default frequencies (2, 4, 6) this is 12, so tickCount cycles 0..11 indefinitely.
     */
    internal val tickModulus: Long = computeTickModulus(config)

    companion object {
        private const val TAG = "CadenceController"

        /** Compute LCM of all tick frequencies so tickCount wraps cleanly. */
        internal fun computeTickModulus(config: CadenceConfig): Long {
            val a = config.syncedToOdooTickFrequency.toLong().coerceAtLeast(1)
            val b = config.telemetryTickFrequency.toLong().coerceAtLeast(1)
            val c = config.configPollTickFrequency.toLong().coerceAtLeast(1)
            return lcm(a, lcm(b, c))
        }

        private fun gcd(a: Long, b: Long): Long = if (b == 0L) a else gcd(b, a % b)
        private fun lcm(a: Long, b: Long): Long = a / gcd(a, b) * b
    }

    /**
     * Start the cadence loop and begin observing connectivity transitions.
     * Idempotent — cancels any existing cadence loop before starting a new one,
     * so repeated calls from [onStartCommand] (e.g. START_STICKY restart) are safe.
     */
    fun start() {
        cadenceJob?.cancel()
        cadenceJob = scope.launch {
            // If adapter has persistent connection, establish it before starting cadence
            if (connectionLifecycle != null) {
                try {
                    connectionLifecycle.setEventListener(fccEventListener)
                    connectionLifecycle.connect()
                    Log.i(TAG, "FCC persistent connection established")
                } catch (e: Exception) {
                    Log.e(TAG, "FCC persistent connection failed on startup — will retry: ${e.message}")
                    launch { attemptReconnect() }
                }
            }

            // Observe connectivity transitions for immediate side effects
            launch { observeConnectivityTransitions() }
            // Main scheduled cadence loop
            runCadenceLoop()
        }
        Log.i(TAG, "CadenceController started (persistentConnection=${connectionLifecycle != null})")
    }

    fun stop() {
        cadenceJob?.cancel()
        // Gracefully disconnect persistent connection if active
        if (connectionLifecycle != null) {
            scope.launch {
                try {
                    connectionLifecycle.setEventListener(null)
                    connectionLifecycle.disconnect()
                    Log.i(TAG, "FCC persistent connection closed")
                } catch (e: Exception) {
                    Log.w(TAG, "Error during FCC disconnect: ${e.message}")
                }
            }
        }
        Log.i(TAG, "CadenceController stopped")
    }

    /**
     * Trigger an immediate FCC poll — called by Odoo POS manual-pull or diagnostic UI.
     * Runs concurrently with (not instead of) the scheduled cadence.
     *
     * M-09: Re-checks connectivity state inside the launched coroutine to avoid
     * acting on a stale snapshot taken at call-site time.
     */
    fun triggerImmediateFccPoll() {
        scope.launch {
            val state = connectivityManager.state.value
            if (state.canPollFcc()) {
                Log.i(TAG, "Immediate FCC poll triggered (state=$state)")
                ingestionOrchestrator.poll()
            } else {
                Log.w(TAG, "Immediate FCC poll requested but FCC not reachable (state=$state)")
            }
        }
    }

    /**
     * Trigger an immediate buffer replay — called after internet recovery or manual trigger.
     * Runs concurrently with the scheduled cadence.
     *
     * M-09: Re-checks connectivity state inside the launched coroutine.
     */
    fun triggerImmediateReplay() {
        scope.launch {
            val state = connectivityManager.state.value
            if (state.hasInternet()) {
                Log.i(TAG, "Immediate replay triggered (state=$state)")
                cloudUploadWorker.uploadPendingBatch()
            } else {
                Log.w(TAG, "Immediate replay requested but internet not reachable (state=$state)")
            }
        }
    }

    // -------------------------------------------------------------------------
    // Connectivity transition side effects per §5.4
    // M-10: Called only from StateFlow observation — no listener interface.
    // -------------------------------------------------------------------------

    internal fun onTransition(from: ConnectivityState, to: ConnectivityState) {
        when (to) {
            ConnectivityState.FULLY_ONLINE -> {
                Log.i(TAG, "→ FULLY_ONLINE: triggering immediate replay + SYNCED_TO_ODOO sync + pre-auth forward")
                scope.launch {
                    // M-08: Reset circuit breakers on internet recovery so workers retry immediately
                    cloudUploadWorker.uploadCircuitBreaker.resetOnConnectivityRecovery()
                    cloudUploadWorker.statusPollCircuitBreaker.resetOnConnectivityRecovery()
                    configPollWorker?.circuitBreaker?.resetOnConnectivityRecovery()
                    preAuthCloudForwardWorker?.circuitBreaker?.resetOnConnectivityRecovery()

                    // Per spec: "Cloud Upload Worker triggers immediate replay of PENDING buffer;
                    // SYNCED_TO_ODOO Poller triggers immediate poll"
                    cloudUploadWorker.uploadPendingBatch()
                    preAuthCloudForwardWorker?.forwardUnsyncedPreAuths()
                    cloudUploadWorker.pollSyncedToOdooStatus()
                    // Telemetry piggybacks on this recovery cycle
                    cloudUploadWorker.reportTelemetry()
                }
            }
            ConnectivityState.FCC_UNREACHABLE -> {
                Log.i(TAG, "→ FCC_UNREACHABLE: FCC poller suspended; cloud workers continue")
                scope.launch {
                    // Internet came up (from FULLY_OFFLINE or INTERNET_DOWN) — reset cloud circuit breakers
                    if (!from.hasInternet()) {
                        cloudUploadWorker.uploadCircuitBreaker.resetOnConnectivityRecovery()
                        cloudUploadWorker.statusPollCircuitBreaker.resetOnConnectivityRecovery()
                        configPollWorker?.circuitBreaker?.resetOnConnectivityRecovery()
                        preAuthCloudForwardWorker?.circuitBreaker?.resetOnConnectivityRecovery()
                    }
                }
            }
            ConnectivityState.INTERNET_DOWN -> {
                Log.i(TAG, "→ INTERNET_DOWN: cloud upload suspended (next tick will skip cloud ops)")
            }
            ConnectivityState.FULLY_OFFLINE -> {
                Log.i(TAG, "→ FULLY_OFFLINE: all cloud+FCC workers suspended; local API continues")
            }
        }
    }

    // -------------------------------------------------------------------------
    // Connectivity observation (calls onTransition for immediate side effects)
    // -------------------------------------------------------------------------

    private suspend fun observeConnectivityTransitions() {
        var prevState = connectivityManager.state.value
        connectivityManager.state.collect { newState ->
            if (newState != prevState) {
                val from = prevState
                prevState = newState
                onTransition(from, newState)
            }
        }
    }

    // -------------------------------------------------------------------------
    // Main cadence loop
    // -------------------------------------------------------------------------

    private suspend fun runCadenceLoop() {
        while (currentCoroutineContext().isActive) {
            val state = connectivityManager.state.value
            val backlogDepth = safeGetBacklogDepth()

            runTick(state, backlogDepth)
            // L-07: Wrap tickCount at LCM of all frequencies to prevent Long overflow
            tickCount = (tickCount + 1) % tickModulus

            val interval = computeInterval(state, backlogDepth)
            Log.d(TAG, "Tick $tickCount done (state=$state, backlog=$backlogDepth, next=${interval}ms)")
            delay(interval)
        }
    }

    /**
     * M-02: Check decommission state between worker calls so that when one worker
     * detects decommission mid-tick, remaining workers are immediately skipped
     * instead of continuing until their own internal check on the next tick.
     */
    private fun isDecommissioned(): Boolean = tokenProvider?.isDecommissioned() == true

    private suspend fun runTick(state: ConnectivityState, backlogDepth: Int) {
        if (isDecommissioned()) {
            Log.w(TAG, "runTick() skipped — device decommissioned")
            return
        }

        when (state) {
            ConnectivityState.FULLY_ONLINE -> {
                ingestionOrchestrator.poll()
                cloudUploadWorker.uploadPendingBatch()
                if (isDecommissioned()) return
                preAuthCloudForwardWorker?.forwardUnsyncedPreAuths()
                if (isDecommissioned()) return
                if (tickCount % config.syncedToOdooTickFrequency == 0L) {
                    // SYNCED_TO_ODOO polling shares the cadence loop with cloud health checks
                    cloudUploadWorker.pollSyncedToOdooStatus()
                    if (isDecommissioned()) return
                }
                if (tickCount % config.telemetryTickFrequency == 0L) {
                    // Telemetry piggybacks on a successful cloud cycle
                    cloudUploadWorker.reportTelemetry()
                }
                if (tickCount % config.configPollTickFrequency == 0L) {
                    configPollWorker?.pollConfig()
                }
            }
            ConnectivityState.INTERNET_DOWN -> {
                // Internet down: FCC still up — poll FCC, buffer locally; no cloud ops
                ingestionOrchestrator.poll()
            }
            ConnectivityState.FCC_UNREACHABLE -> {
                // FCC unreachable: internet up — upload existing buffer, sync status from cloud
                cloudUploadWorker.uploadPendingBatch()
                if (isDecommissioned()) return
                preAuthCloudForwardWorker?.forwardUnsyncedPreAuths()
                if (isDecommissioned()) return
                if (tickCount % config.syncedToOdooTickFrequency == 0L) {
                    cloudUploadWorker.pollSyncedToOdooStatus()
                    if (isDecommissioned()) return
                }
                if (tickCount % config.configPollTickFrequency == 0L) {
                    configPollWorker?.pollConfig()
                }
            }
            ConnectivityState.FULLY_OFFLINE -> {
                // Both down — no cloud or FCC work; local API continues independently
            }
        }

        // Pre-auth expiry check runs every tick regardless of connectivity state.
        // Fast under normal conditions: index-backed query returns empty set immediately.
        preAuthHandler?.runExpiryCheck()
    }

    // -------------------------------------------------------------------------
    // Interval computation
    // -------------------------------------------------------------------------

    private fun computeInterval(state: ConnectivityState, backlogDepth: Int): Long {
        val base = when {
            state == ConnectivityState.FULLY_OFFLINE -> config.offlineIntervalMs
            backlogDepth > config.highBacklogThreshold -> config.highBacklogIntervalMs
            else -> config.baseIntervalMs
        }
        val jitter = if (config.jitterRangeMs <= 0L) 0L else Random.nextLong(0L, config.jitterRangeMs)
        return base + jitter
    }

    // -------------------------------------------------------------------------
    // Persistent connection reconnect logic
    // -------------------------------------------------------------------------

    /**
     * Attempt to re-establish a lost persistent FCC connection with exponential backoff.
     * Called from onConnectionLost callback and from startup failure recovery.
     */
    private suspend fun attemptReconnect() {
        val lifecycle = connectionLifecycle ?: return
        var attempt = 0
        val maxBackoffMs = 60_000L

        while (currentCoroutineContext().isActive && !lifecycle.isConnected) {
            attempt++
            val backoffMs = (1_000L * (1L shl attempt.coerceAtMost(6))).coerceAtMost(maxBackoffMs)
            Log.i(TAG, "FCC reconnect attempt $attempt in ${backoffMs}ms")
            delay(backoffMs)

            try {
                lifecycle.connect()
                Log.i(TAG, "FCC reconnect succeeded on attempt $attempt")
                return
            } catch (e: Exception) {
                Log.w(TAG, "FCC reconnect attempt $attempt failed: ${e.message}")
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private suspend fun safeGetBacklogDepth(): Int {
        return try {
            transactionDao.countForLocalApi()
        } catch (e: Exception) {
            0
        }
    }
}

// ConnectivityState extension helpers

private fun ConnectivityState.hasInternet(): Boolean =
    this == ConnectivityState.FULLY_ONLINE || this == ConnectivityState.FCC_UNREACHABLE

private fun ConnectivityState.canPollFcc(): Boolean =
    this == ConnectivityState.FULLY_ONLINE || this == ConnectivityState.INTERNET_DOWN

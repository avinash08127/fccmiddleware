package com.fccmiddleware.edge.runtime

import android.util.Log
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.connectivity.ConnectivityTransitionListener
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.preauth.PreAuthHandler
import com.fccmiddleware.edge.sync.CloudUploadWorker
import com.fccmiddleware.edge.sync.ConfigPollWorker
import com.fccmiddleware.edge.sync.PreAuthCloudForwardWorker
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
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
 * Implements [ConnectivityTransitionListener] so it can be registered with
 * [ConnectivityManager] for immediate reaction to state changes.
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
    val config: CadenceConfig = CadenceConfig(),
) : ConnectivityTransitionListener {

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

    companion object {
        private const val TAG = "CadenceController"
    }

    /**
     * Start the cadence loop and begin observing connectivity transitions.
     * Call once from the foreground service [onStartCommand].
     */
    fun start() {
        cadenceJob = scope.launch {
            // Observe connectivity transitions for immediate side effects
            launch { observeConnectivityTransitions() }
            // Main scheduled cadence loop
            runCadenceLoop()
        }
        Log.i(TAG, "CadenceController started")
    }

    fun stop() {
        cadenceJob?.cancel()
        Log.i(TAG, "CadenceController stopped")
    }

    /**
     * Trigger an immediate FCC poll — called by Odoo POS manual-pull or diagnostic UI.
     * Runs concurrently with (not instead of) the scheduled cadence.
     */
    fun triggerImmediateFccPoll() {
        val state = connectivityManager.state.value
        if (state.canPollFcc()) {
            scope.launch {
                Log.i(TAG, "Immediate FCC poll triggered")
                ingestionOrchestrator.poll()
            }
        } else {
            Log.w(TAG, "Immediate FCC poll requested but FCC not reachable (state=$state)")
        }
    }

    /**
     * Trigger an immediate buffer replay — called after internet recovery or manual trigger.
     * Runs concurrently with the scheduled cadence.
     */
    fun triggerImmediateReplay() {
        val state = connectivityManager.state.value
        if (state.hasInternet()) {
            scope.launch {
                Log.i(TAG, "Immediate replay triggered")
                cloudUploadWorker.uploadPendingBatch()
            }
        } else {
            Log.w(TAG, "Immediate replay requested but internet not reachable (state=$state)")
        }
    }

    // -------------------------------------------------------------------------
    // ConnectivityTransitionListener — immediate side effects per §5.4
    // -------------------------------------------------------------------------

    override fun onTransition(from: ConnectivityState, to: ConnectivityState) {
        when (to) {
            ConnectivityState.FULLY_ONLINE -> {
                Log.i(TAG, "→ FULLY_ONLINE: triggering immediate replay + SYNCED_TO_ODOO sync + pre-auth forward")
                scope.launch {
                    // Per spec: "Cloud Upload Worker triggers immediate replay of PENDING buffer;
                    // SYNCED_TO_ODOO Poller triggers immediate poll"
                    cloudUploadWorker.uploadPendingBatch()
                    preAuthCloudForwardWorker?.forwardUnsyncedPreAuths()
                    cloudUploadWorker.pollSyncedToOdooStatus()
                    // Telemetry piggybacks on this recovery cycle
                    cloudUploadWorker.reportTelemetry()
                }
            }
            ConnectivityState.INTERNET_DOWN -> {
                Log.i(TAG, "→ INTERNET_DOWN: cloud upload suspended (next tick will skip cloud ops)")
            }
            ConnectivityState.FCC_UNREACHABLE -> {
                Log.i(TAG, "→ FCC_UNREACHABLE: FCC poller suspended (next tick will skip FCC ops)")
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
        while (coroutineContext.isActive) {
            val state = connectivityManager.state.value
            val backlogDepth = safeGetBacklogDepth()

            runTick(state, backlogDepth)
            tickCount++

            val interval = computeInterval(state, backlogDepth)
            Log.d(TAG, "Tick $tickCount done (state=$state, backlog=$backlogDepth, next=${interval}ms)")
            delay(interval)
        }
    }

    private suspend fun runTick(state: ConnectivityState, backlogDepth: Int) {
        when (state) {
            ConnectivityState.FULLY_ONLINE -> {
                ingestionOrchestrator.poll()
                cloudUploadWorker.uploadPendingBatch()
                preAuthCloudForwardWorker?.forwardUnsyncedPreAuths()
                if (tickCount % config.syncedToOdooTickFrequency == 0L) {
                    // SYNCED_TO_ODOO polling shares the cadence loop with cloud health checks
                    cloudUploadWorker.pollSyncedToOdooStatus()
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
                preAuthCloudForwardWorker?.forwardUnsyncedPreAuths()
                if (tickCount % config.syncedToOdooTickFrequency == 0L) {
                    cloudUploadWorker.pollSyncedToOdooStatus()
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
        return base + Random.nextLong(0, config.jitterRangeMs)
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

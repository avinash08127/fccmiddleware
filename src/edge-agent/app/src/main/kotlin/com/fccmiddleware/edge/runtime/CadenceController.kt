package com.fccmiddleware.edge.runtime

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.adapter.common.IFccConnectionLifecycle
import com.fccmiddleware.edge.adapter.common.IFccEventListener
import com.fccmiddleware.edge.adapter.common.PumpState
import com.fccmiddleware.edge.adapter.common.TransactionNotification
import com.fccmiddleware.edge.buffer.CleanupWorker
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.logging.StructuredFileLogger
import com.fccmiddleware.edge.preauth.PreAuthHandler
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.CloudUploadWorker
import com.fccmiddleware.edge.sync.CloudVersionCheckResult
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
    /** Cloud API client — used for version check on startup. */
    private val cloudApiClient: CloudApiClient? = null,
    /** Agent version in semantic format (e.g. "1.0.0"). Used for version check on startup. */
    private val agentVersion: String? = null,
    /** AF-034: Cleanup worker — retention, quota enforcement, stale UPLOADED revert. */
    private val cleanupWorker: CleanupWorker? = null,
    /** AF-034: File logger — log rotation is triggered alongside cleanup. */
    private val fileLogger: StructuredFileLogger? = null,
    fccAdapter: IFccAdapter? = null,
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
        /** AP-004: Run fiscalization retry every N ticks (~2 min at 30s base) to avoid
         *  blocking the main cadence tick with sequential Advatec HTTP calls. */
        val fiscalRetryTickFrequency: Int = 4,
        /** AF-034: Run cleanup every N ticks. 2880 ticks * 30s ≈ 24 hours. */
        val cleanupTickFrequency: Int = 2880,
    )

    /**
     * Version compatibility flag. Set to false when the cloud reports this agent version
     * is below the minimum supported version. When false, FCC communication is disabled
     * to prevent data format mismatches per requirements §15.13.
     * Defaults to true (fail-open) — FCC is allowed until an explicit incompatibility is detected.
     */
    @Volatile
    internal var versionCompatible: Boolean = true

    /**
     * Callback invoked when FCC reconnect is requested (e.g., after settings change).
     * The service wires this to rebuild the adapter with current config + local overrides.
     * When set, [requestFccReconnect] delegates to this callback for a full adapter rebuild
     * rather than just disconnecting/reconnecting the existing adapter instance.
     */
    @Volatile
    var onFccReconnectRequested: (suspend () -> Unit)? = null

    /** Late-bound: wired when FCC config becomes available after startup. */
    @Volatile
    internal var fccAdapter: IFccAdapter? = fccAdapter

    internal fun updateFccAdapter(adapter: IFccAdapter?) {
        val previousLifecycle = connectionLifecycle
        fccAdapter = adapter
        val nextLifecycle = connectionLifecycle

        if (cadenceJob == null) {
            return
        }

        scope.launch {
            if (previousLifecycle != null && previousLifecycle !== nextLifecycle) {
                try {
                    previousLifecycle.setEventListener(null)
                    previousLifecycle.disconnect()
                    AppLogger.i(TAG, "Disconnected previous FCC runtime")
                } catch (e: Exception) {
                    AppLogger.w(TAG, "Failed to disconnect previous FCC runtime: ${e.message}")
                }
            }

            if (nextLifecycle != null && previousLifecycle !== nextLifecycle) {
                try {
                    nextLifecycle.setEventListener(fccEventListener)
                    nextLifecycle.connect()
                    AppLogger.i(TAG, "Connected updated FCC runtime")
                } catch (e: Exception) {
                    AppLogger.e(TAG, "Failed to connect updated FCC runtime: ${e.message}")
                    launch { attemptReconnect() }
                }
            }
        }
    }

    private var cadenceJob: Job? = null
    private var tickCount = 0L

    /**
     * Computed from current [fccAdapter]: non-null only when the adapter supports persistent connections.
     * Used to call connect/disconnect and wire event listener callbacks.
     */
    private val connectionLifecycle: IFccConnectionLifecycle?
        get() = fccAdapter as? IFccConnectionLifecycle

    /** Event listener that bridges unsolicited FCC events into the cadence controller. */
    private val fccEventListener = object : IFccEventListener {
        override fun onPumpStatusChanged(pumpNumber: Int, newState: PumpState, fccStatusCode: String?) {
            AppLogger.d(TAG, "FCC pump $pumpNumber status -> $newState (fccCode=$fccStatusCode)")
        }

        override fun onTransactionAvailable(notification: TransactionNotification) {
            AppLogger.i(TAG, "FCC transaction available: fp=${notification.fpId}, idx=${notification.transactionBufferIndex}")
            triggerImmediateFccPoll()
        }

        override fun onFuellingUpdate(pumpNumber: Int, volumeMicrolitres: Long, amountMinorUnits: Long) {
            AppLogger.d(TAG, "FCC fuelling update: pump=$pumpNumber, vol=$volumeMicrolitres µL, amt=$amountMinorUnits")
        }

        override fun onConnectionLost(reason: String) {
            AppLogger.w(TAG, "FCC connection lost: $reason — marking unreachable, scheduling reconnect")
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
            val d = config.fiscalRetryTickFrequency.toLong().coerceAtLeast(1)
            val e = config.cleanupTickFrequency.toLong().coerceAtLeast(1)
            return lcm(a, lcm(b, lcm(c, lcm(d, e))))
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
            // Version check on startup — per requirements §15.13, agent calls
            // /agent/version-check on startup and disables FCC communication if
            // below minimum supported version.
            performStartupVersionCheck()

            // If adapter has persistent connection, establish it before starting cadence
            val lifecycle = connectionLifecycle
            if (lifecycle != null && versionCompatible) {
                try {
                    lifecycle.setEventListener(fccEventListener)
                    lifecycle.connect()
                    AppLogger.i(TAG, "FCC persistent connection established")
                } catch (e: Exception) {
                    AppLogger.e(TAG, "FCC persistent connection failed on startup — will retry: ${e.message}")
                    launch { attemptReconnect() }
                }
            }

            // Observe connectivity transitions for immediate side effects
            launch { observeConnectivityTransitions() }
            // Main scheduled cadence loop
            runCadenceLoop()
        }
        AppLogger.i(TAG, "CadenceController started (persistentConnection=${connectionLifecycle != null})")
    }

    fun stop() {
        cadenceJob?.cancel()
        // Gracefully disconnect persistent connection if active
        val lifecycle = connectionLifecycle
        if (lifecycle != null) {
            scope.launch {
                try {
                    lifecycle.setEventListener(null)
                    lifecycle.disconnect()
                    AppLogger.i(TAG, "FCC persistent connection closed")
                } catch (e: Exception) {
                    AppLogger.w(TAG, "Error during FCC disconnect: ${e.message}")
                }
            }
        }
        AppLogger.i(TAG, "CadenceController stopped")
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
                AppLogger.i(TAG, "Immediate FCC poll triggered (state=$state)")
                ingestionOrchestrator.poll()
            } else {
                AppLogger.w(TAG, "Immediate FCC poll requested but FCC not reachable (state=$state)")
            }
        }
    }

    /**
     * Request FCC adapter disconnect and reconnect with current config (including local overrides).
     * Called from SettingsActivity after saving new override values.
     *
     * When [onFccReconnectRequested] is wired (by the service), this triggers a full adapter
     * rebuild with the current config + local overrides, ensuring the new connection parameters
     * are actually used. Otherwise falls back to a simple disconnect/reconnect of the same adapter.
     */
    fun requestFccReconnect() {
        scope.launch {
            AppLogger.i(TAG, "FCC reconnect requested (settings changed)")

            // Prefer full adapter rebuild (picks up new override values)
            val rebuildCallback = onFccReconnectRequested
            if (rebuildCallback != null) {
                AppLogger.i(TAG, "Rebuilding FCC adapter with current config + overrides")
                rebuildCallback()
                return@launch
            }

            // Fallback: simple disconnect/reconnect of the existing adapter instance
            val lifecycle = connectionLifecycle
            if (lifecycle == null) {
                AppLogger.w(TAG, "requestFccReconnect: no persistent connection lifecycle — skipping")
                return@launch
            }

            try {
                lifecycle.setEventListener(null)
                lifecycle.disconnect()
                AppLogger.i(TAG, "FCC disconnected for reconnect")
            } catch (e: Exception) {
                AppLogger.w(TAG, "Error disconnecting FCC for reconnect: ${e.message}")
            }

            try {
                lifecycle.setEventListener(fccEventListener)
                lifecycle.connect()
                AppLogger.i(TAG, "FCC reconnected with updated config")
            } catch (e: Exception) {
                AppLogger.e(TAG, "FCC reconnect failed — scheduling retry: ${e.message}")
                launch { attemptReconnect() }
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
                AppLogger.i(TAG, "Immediate replay triggered (state=$state)")
                cloudUploadWorker.uploadPendingBatch()
            } else {
                AppLogger.w(TAG, "Immediate replay requested but internet not reachable (state=$state)")
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
                AppLogger.i(TAG, "→ FULLY_ONLINE: triggering immediate replay + SYNCED_TO_ODOO sync + pre-auth forward")
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
                    cloudUploadWorker.reportDiagnosticLogs()
                }
            }
            ConnectivityState.FCC_UNREACHABLE -> {
                AppLogger.i(TAG, "→ FCC_UNREACHABLE: FCC poller suspended; cloud workers continue")
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
                AppLogger.i(TAG, "→ INTERNET_DOWN: cloud upload suspended (next tick will skip cloud ops)")
            }
            ConnectivityState.FULLY_OFFLINE -> {
                AppLogger.i(TAG, "→ FULLY_OFFLINE: all cloud+FCC workers suspended; local API continues")
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
            AppLogger.d(TAG, "Tick $tickCount done (state=$state, backlog=$backlogDepth, next=${interval}ms)")
            delay(interval)
        }
    }

    /**
     * M-02: Check decommission state between worker calls so that when one worker
     * detects decommission mid-tick, remaining workers are immediately skipped
     * instead of continuing until their own internal check on the next tick.
     */
    private fun isDecommissioned(): Boolean = tokenProvider?.isDecommissioned() == true

    private fun isReprovisioningRequired(): Boolean = tokenProvider?.isReprovisioningRequired() == true

    private suspend fun runTick(state: ConnectivityState, backlogDepth: Int) {
        if (isDecommissioned()) {
            AppLogger.w(TAG, "runTick() skipped — device decommissioned")
            return
        }

        if (isReprovisioningRequired()) {
            AppLogger.w(TAG, "runTick() skipped — re-provisioning required (refresh token expired)")
            return
        }

        if (!versionCompatible) {
            AppLogger.w(TAG, "runTick() — FCC communication disabled (agent version below minimum). " +
                "Cloud operations continue for telemetry and config updates.")
            // Allow cloud operations (upload existing buffer, config poll, telemetry)
            // but skip FCC polling to prevent data format mismatches.
            if (state.hasInternet()) {
                cloudUploadWorker.uploadPendingBatch()
                if (isDecommissioned()) return
                if (tickCount % config.telemetryTickFrequency == 0L) {
                    cloudUploadWorker.reportTelemetry()
                    cloudUploadWorker.reportDiagnosticLogs()
                }
                if (tickCount % config.configPollTickFrequency == 0L) {
                    configPollWorker?.pollConfig()
                }
            }
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
                    cloudUploadWorker.reportDiagnosticLogs()
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
                // AF-036: Send telemetry during FCC_UNREACHABLE — cloud needs visibility into
                // FCC connection errors, heartbeat failures, and buffer backlog during outages.
                if (tickCount % config.telemetryTickFrequency == 0L) {
                    cloudUploadWorker.reportTelemetry()
                    cloudUploadWorker.reportDiagnosticLogs()
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

        // AP-004: Fiscalization retry runs on its own cadence to avoid blocking the main
        // tick with sequential Advatec HTTP calls. Only runs when FCC is reachable since
        // the Advatec device is on the LAN.
        if (state.canPollFcc() && tickCount % config.fiscalRetryTickFrequency == 0L) {
            ingestionOrchestrator.retryPendingFiscalization()
        }

        // AF-034: Periodic cleanup — retention, quota enforcement, stale UPLOADED revert,
        // and log file rotation. Runs regardless of connectivity state since all operations
        // are local. Default: every 2880 ticks ≈ 24 hours at 30s base interval.
        if (tickCount % config.cleanupTickFrequency == 0L) {
            try {
                cleanupWorker?.runCleanup()
                fileLogger?.rotateOldFiles()
            } catch (e: Exception) {
                AppLogger.e(TAG, "Cleanup tick failed: ${e.message}", e)
            }
        }
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
            AppLogger.i(TAG, "FCC reconnect attempt $attempt in ${backoffMs}ms")
            delay(backoffMs)

            try {
                lifecycle.connect()
                AppLogger.i(TAG, "FCC reconnect succeeded on attempt $attempt")
                return
            } catch (e: Exception) {
                AppLogger.w(TAG, "FCC reconnect attempt $attempt failed: ${e.message}")
            }
        }
    }

    // -------------------------------------------------------------------------
    // Startup version check
    // -------------------------------------------------------------------------

    /**
     * Per requirements §15.13: call /agent/version-check on startup.
     * If agent version is below minimum supported:
     *   - Log a critical warning (alert site supervisor)
     *   - Disable FCC communication until updated
     * Fail-open: if the check cannot be completed (no token, network error),
     * FCC communication remains enabled.
     */
    private suspend fun performStartupVersionCheck() {
        val client = cloudApiClient ?: return
        val version = agentVersion ?: return
        val tp = tokenProvider ?: return

        val token = tp.getAccessToken()
        if (token == null) {
            AppLogger.w(TAG, "Version check skipped: no device token available (not yet registered)")
            return
        }

        AppLogger.i(TAG, "Performing startup version check (agentVersion=$version)")
        val result = client.checkVersion(version, token)

        when (result) {
            is CloudVersionCheckResult.Success -> {
                handleVersionCheckResponse(result.response, version)
            }
            is CloudVersionCheckResult.Unauthorized -> {
                // Token may be expired; try refresh once
                AppLogger.d(TAG, "Version check received 401; refreshing token and retrying")
                val refreshed = tp.refreshAccessToken()
                if (refreshed) {
                    val newToken = tp.getAccessToken()
                    if (newToken != null) {
                        val retryResult = client.checkVersion(version, newToken)
                        if (retryResult is CloudVersionCheckResult.Success) {
                            handleVersionCheckResponse(retryResult.response, version)
                        } else {
                            AppLogger.w(TAG, "Version check failed after token refresh — allowing FCC (fail-open)")
                        }
                    }
                } else {
                    AppLogger.w(TAG, "Version check skipped: token refresh failed — allowing FCC (fail-open)")
                }
            }
            is CloudVersionCheckResult.TransportError -> {
                AppLogger.w(TAG, "Version check failed: ${result.message} — allowing FCC (fail-open)")
            }
        }
    }

    private fun handleVersionCheckResponse(resp: com.fccmiddleware.edge.sync.VersionCheckResponse, version: String) {
        if (!resp.compatible) {
            versionCompatible = false
            AppLogger.e(
                TAG,
                "VERSION INCOMPATIBLE: agent version $version is below minimum " +
                    "${resp.minimumVersion}. FCC communication DISABLED to prevent " +
                    "data format mismatches. Update agent to at least ${resp.minimumVersion}." +
                    (resp.updateUrl?.let { " Download: $it" } ?: ""),
            )
        } else {
            versionCompatible = true
            if (resp.updateAvailable) {
                AppLogger.i(
                    TAG,
                    "Version check passed (compatible). Update available: " +
                        "${resp.latestVersion}" +
                        (resp.releaseNotes?.let { " — $it" } ?: ""),
                )
            } else {
                AppLogger.i(TAG, "Version check passed (compatible, up to date)")
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

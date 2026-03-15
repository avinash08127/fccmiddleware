package com.fccmiddleware.edge.peer

import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.PeerDirectoryEntryDto
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.logging.StructuredFileLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZoneOffset
import java.util.concurrent.ConcurrentHashMap

/**
 * PeerCoordinator — manages HA peer state, heartbeat exchange, and leadership tracking.
 *
 * Responsibilities:
 * - Maintains an in-memory directory of known peers and their health status.
 * - Builds health responses for incoming GET /peer/health requests.
 * - Processes incoming heartbeats and leadership claims from peers.
 * - Sends outbound heartbeats to all known peers on each cadence tick.
 * - Evaluates peers for suspect/down classification based on missed heartbeats.
 * - Provides [isPrimarySuspected] for the election coordinator to trigger failover.
 *
 * Thread safety: peer directory is a [ConcurrentHashMap]; individual fields on
 * [PeerState] are replaced atomically via copy-on-write (data class copy).
 */
class PeerCoordinator(
    private val peerHttpClient: PeerHttpClient,
    private val configManager: ConfigManager,
    private val encryptedPrefsManager: EncryptedPrefsManager,
    private val fileLogger: StructuredFileLogger,
) {

    companion object {
        private const val TAG = "PeerCoordinator"
        /** Number of missed heartbeats before a peer is SUSPECTED. */
        private const val SUSPECT_THRESHOLD = 3
        /** Number of missed heartbeats before a peer is CONFIRMED_DOWN. */
        private const val DOWN_THRESHOLD = 6
    }

    /** In-memory directory of known peers keyed by agentId. */
    val peers: ConcurrentHashMap<String, PeerState> = ConcurrentHashMap()

    /** The local agent's current HA role. Updated from config and election results. */
    @Volatile
    var currentRole: String = "STANDBY_HOT"

    /** The current leader epoch. Updated from heartbeats and election results. */
    @Volatile
    var leaderEpoch: Long = 0

    /** The agent ID of the current leader. */
    @Volatile
    var leaderAgentId: String? = null

    /** Monotonic sequence for the local agent's high-water-mark. */
    @Volatile
    var highWaterMarkSeq: Long = 0

    /** Service start time for uptime reporting. */
    private val startedAtMs = System.currentTimeMillis()

    /** Whether this coordinator has been initialized from config. */
    @Volatile
    var initialized = false
        private set

    // -------------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------------

    /**
     * Initialize peer directory and local role from the current site HA config.
     * Called once during startup after config is loaded.
     */
    fun initializeFromConfig() {
        val config = configManager.config.value ?: return
        val haConfig = config.siteHa
        if (!haConfig.enabled) return

        currentRole = haConfig.currentRole
        leaderEpoch = haConfig.leaderEpoch
        leaderAgentId = haConfig.leaderAgentId

        val localAgentId = encryptedPrefsManager.deviceId ?: return

        for (entry in haConfig.peerDirectory) {
            if (entry.agentId == localAgentId) continue
            val baseUrl = entry.peerApiBaseUrl ?: buildBaseUrl(entry)
            peers[entry.agentId] = PeerState(
                agentId = entry.agentId,
                deviceClass = entry.deviceClass,
                currentRole = entry.currentRole,
                leaderEpoch = entry.leaderEpochSeen ?: haConfig.leaderEpoch,
                peerApiBaseUrl = baseUrl,
            )
        }

        initialized = true
        AppLogger.i(TAG, "Initialized with ${peers.size} peer(s), role=$currentRole, epoch=$leaderEpoch")
    }

    // -------------------------------------------------------------------------
    // Health response (GET /peer/health)
    // -------------------------------------------------------------------------

    /**
     * Build a health response for incoming peer health checks.
     */
    fun buildHealthResponse(): PeerHealthResponse {
        val config = configManager.config.value
        val agentId = encryptedPrefsManager.deviceId ?: "unknown"
        val siteCode = encryptedPrefsManager.siteCode ?: "unknown"
        val fccReachable = true // Simplified — real implementation checks FCC adapter
        val uptimeSeconds = (System.currentTimeMillis() - startedAtMs) / 1000L
        val appVersion = config?.rollout?.minAgentVersion ?: "1.0.0"

        return PeerHealthResponse(
            agentId = agentId,
            siteCode = siteCode,
            currentRole = currentRole,
            leaderEpoch = leaderEpoch,
            fccReachable = fccReachable,
            uptimeSeconds = uptimeSeconds,
            appVersion = appVersion,
            highWaterMarkSeq = highWaterMarkSeq,
            reportedAtUtc = Instant.now().toString(),
        )
    }

    // -------------------------------------------------------------------------
    // Incoming heartbeat (POST /peer/heartbeat)
    // -------------------------------------------------------------------------

    /**
     * Process a heartbeat received from a peer.
     * Updates the peer directory and returns a response.
     */
    fun handleIncomingHeartbeat(request: PeerHeartbeatRequest): PeerHeartbeatResponse {
        val localAgentId = encryptedPrefsManager.deviceId ?: "unknown"
        val localSiteCode = encryptedPrefsManager.siteCode ?: "unknown"

        // Validate site code
        if (request.siteCode != localSiteCode) {
            AppLogger.w(TAG, "Heartbeat rejected: site code mismatch (${request.siteCode} != $localSiteCode)")
            return PeerHeartbeatResponse(
                agentId = localAgentId,
                currentRole = currentRole,
                leaderEpoch = leaderEpoch,
                accepted = false,
                receivedAtUtc = Instant.now().toString(),
            )
        }

        // Update peer state
        val now = Instant.now()
        peers.compute(request.agentId) { _, existing ->
            (existing ?: PeerState(
                agentId = request.agentId,
                deviceClass = request.deviceClass,
                currentRole = request.currentRole,
                leaderEpoch = request.leaderEpoch,
                peerApiBaseUrl = null,
            )).copy(
                currentRole = request.currentRole,
                leaderEpoch = request.leaderEpoch,
                lastHeartbeatReceivedAt = now,
                consecutiveMissedHeartbeats = 0,
                suspectStatus = SuspectStatus.HEALTHY,
                replicationLagSeconds = request.replicationLagSeconds,
            )
        }

        // Accept epoch advancement from heartbeat
        if (request.leaderEpoch > leaderEpoch) {
            leaderEpoch = request.leaderEpoch
            leaderAgentId = request.leaderAgentId
            AppLogger.i(TAG, "Epoch advanced to ${request.leaderEpoch} via heartbeat from ${request.agentId}")
        }

        return PeerHeartbeatResponse(
            agentId = localAgentId,
            currentRole = currentRole,
            leaderEpoch = leaderEpoch,
            accepted = true,
            receivedAtUtc = now.toString(),
        )
    }

    // -------------------------------------------------------------------------
    // Incoming leadership claim (POST /peer/claim-leadership)
    // -------------------------------------------------------------------------

    /**
     * Process a leadership claim from a candidate peer.
     *
     * Acceptance rules:
     * - Proposed epoch must be strictly greater than current epoch.
     * - If local agent is PRIMARY with same epoch, reject (split-brain prevention).
     */
    fun handleLeadershipClaim(request: PeerLeadershipClaimRequest): PeerLeadershipClaimResponse {
        val localSiteCode = encryptedPrefsManager.siteCode ?: "unknown"

        // Site code must match
        if (request.siteCode != localSiteCode) {
            return PeerLeadershipClaimResponse(
                accepted = false,
                reason = "Site code mismatch",
                currentEpoch = leaderEpoch,
            )
        }

        // Epoch must be strictly greater
        if (request.proposedEpoch <= leaderEpoch) {
            return PeerLeadershipClaimResponse(
                accepted = false,
                reason = "Proposed epoch ${request.proposedEpoch} <= current $leaderEpoch",
                currentEpoch = leaderEpoch,
            )
        }

        // If we are PRIMARY at the current epoch, reject to prevent split-brain
        if (currentRole == "PRIMARY" && request.proposedEpoch == leaderEpoch + 1) {
            // We're still alive as primary — reject unless we've already stepped down
            val localAgentId = encryptedPrefsManager.deviceId
            if (localAgentId != null && localAgentId != request.candidateAgentId) {
                AppLogger.w(TAG, "Rejecting claim from ${request.candidateAgentId}: we are still PRIMARY at epoch $leaderEpoch")
                return PeerLeadershipClaimResponse(
                    accepted = false,
                    reason = "Current PRIMARY is still alive",
                    currentEpoch = leaderEpoch,
                )
            }
        }

        // Accept the claim
        leaderEpoch = request.proposedEpoch
        leaderAgentId = request.candidateAgentId
        AppLogger.i(TAG, "Accepted leadership claim from ${request.candidateAgentId} at epoch ${request.proposedEpoch}")

        return PeerLeadershipClaimResponse(
            accepted = true,
            reason = null,
            currentEpoch = leaderEpoch,
        )
    }

    // -------------------------------------------------------------------------
    // Outbound heartbeat (called by CadenceController each tick)
    // -------------------------------------------------------------------------

    /**
     * Send heartbeats to all known peers. Updates suspect status for peers
     * that fail to respond. Called from the cadence loop.
     */
    fun sendHeartbeatToAllPeers() {
        if (!initialized) return
        val localAgentId = encryptedPrefsManager.deviceId ?: return
        val localSiteCode = encryptedPrefsManager.siteCode ?: return
        val config = configManager.config.value ?: return
        val uptimeSeconds = (System.currentTimeMillis() - startedAtMs) / 1000L

        val request = PeerHeartbeatRequest(
            agentId = localAgentId,
            siteCode = localSiteCode,
            currentRole = currentRole,
            leaderEpoch = leaderEpoch,
            leaderAgentId = leaderAgentId,
            configVersion = config.configId,
            replicationLagSeconds = 0.0,
            lastSequenceApplied = highWaterMarkSeq,
            deviceClass = config.identity.deviceClass,
            appVersion = config.rollout.minAgentVersion,
            uptimeSeconds = uptimeSeconds,
            sentAtUtc = OffsetDateTime.now(ZoneOffset.UTC).toString(),
        )

        for ((agentId, peerState) in peers) {
            val baseUrl = peerState.peerApiBaseUrl ?: continue
            val response = peerHttpClient.sendHeartbeat(baseUrl, request)
            if (response != null && response.accepted) {
                // Heartbeat acknowledged — peer is alive
                peers.computeIfPresent(agentId) { _, existing ->
                    existing.copy(
                        currentRole = response.currentRole,
                        leaderEpoch = response.leaderEpoch,
                    )
                }
            } else {
                // Heartbeat failed — increment missed count
                peers.computeIfPresent(agentId) { _, existing ->
                    val newMissed = existing.consecutiveMissedHeartbeats + 1
                    existing.copy(
                        consecutiveMissedHeartbeats = newMissed,
                        suspectStatus = when {
                            newMissed >= DOWN_THRESHOLD -> SuspectStatus.CONFIRMED_DOWN
                            newMissed >= SUSPECT_THRESHOLD -> SuspectStatus.SUSPECTED
                            else -> SuspectStatus.HEALTHY
                        },
                    )
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Suspect evaluation
    // -------------------------------------------------------------------------

    /**
     * Whether the current PRIMARY peer is suspected or confirmed down.
     * Used by [ElectionCoordinator] to decide whether to trigger failover.
     */
    val isPrimarySuspected: Boolean
        get() {
            val primaryAgentId = leaderAgentId ?: return false
            val localAgentId = encryptedPrefsManager.deviceId
            if (primaryAgentId == localAgentId) return false // We are primary
            val primaryState = peers[primaryAgentId] ?: return true // Unknown primary = suspected
            return primaryState.suspectStatus != SuspectStatus.HEALTHY
        }

    /**
     * Direct health probe to a specific peer (bypasses heartbeat cadence).
     * Used for confirming a suspect before triggering election.
     *
     * @return true if the peer responds to a health check, false otherwise.
     */
    fun directHealthProbe(agentId: String): Boolean {
        val peerState = peers[agentId] ?: return false
        val baseUrl = peerState.peerApiBaseUrl ?: return false
        val health = peerHttpClient.getHealth(baseUrl)
        return health != null
    }

    /**
     * Evaluate all peers and update their suspect status based on missed heartbeats.
     * Called periodically alongside heartbeat sending.
     */
    fun evaluateSuspects() {
        for ((agentId, peerState) in peers) {
            val lastHb = peerState.lastHeartbeatReceivedAt ?: continue
            val config = configManager.config.value ?: continue
            val heartbeatInterval = config.siteHa.heartbeatIntervalSeconds.toLong()
            val elapsedSeconds = java.time.Duration.between(lastHb, Instant.now()).seconds

            if (elapsedSeconds > heartbeatInterval * DOWN_THRESHOLD) {
                peers.computeIfPresent(agentId) { _, existing ->
                    if (existing.suspectStatus != SuspectStatus.CONFIRMED_DOWN) {
                        AppLogger.w(TAG, "Peer $agentId CONFIRMED_DOWN (${elapsedSeconds}s since last heartbeat)")
                        fileLogger.w(TAG, "Peer $agentId CONFIRMED_DOWN (${elapsedSeconds}s since last heartbeat)")
                    }
                    existing.copy(suspectStatus = SuspectStatus.CONFIRMED_DOWN)
                }
            } else if (elapsedSeconds > heartbeatInterval * SUSPECT_THRESHOLD) {
                peers.computeIfPresent(agentId) { _, existing ->
                    if (existing.suspectStatus == SuspectStatus.HEALTHY) {
                        AppLogger.w(TAG, "Peer $agentId SUSPECTED (${elapsedSeconds}s since last heartbeat)")
                    }
                    existing.copy(suspectStatus = SuspectStatus.SUSPECTED)
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun buildBaseUrl(entry: PeerDirectoryEntryDto): String? {
        val host = entry.peerApiAdvertisedHost ?: return null
        val port = entry.peerApiPort ?: return null
        val scheme = if (entry.peerApiTlsEnabled) "https" else "http"
        return "$scheme://$host:$port"
    }
}

package com.fccmiddleware.edge.adapter.doms.jpl

import com.fccmiddleware.edge.logging.AppLogger
import kotlinx.coroutines.*

/**
 * Manages periodic JPL heartbeat frames and detects dead connections.
 *
 * Sends a heartbeat (empty STX/ETX frame) at the configured interval.
 * Detects dead connections when no frame (data or heartbeat) has been received
 * within 3× the heartbeat interval.
 *
 * Lifecycle: create → start() → ... → stop()
 */
class JplHeartbeatManager(
    private val tcpClient: JplTcpClient,
    private val scope: CoroutineScope,
    private val intervalSeconds: Int = 30,
) {
    companion object {
        private const val TAG = "JplHeartbeatManager"
        /** Initial dead connection multiplier (increases with consecutive misses). */
        private const val INITIAL_DEAD_MULTIPLIER = 3
        /** Maximum dead connection multiplier cap. */
        private const val MAX_DEAD_MULTIPLIER = 10
        /** Maximum jitter as a fraction of the threshold (0.0–1.0). */
        private const val JITTER_FRACTION = 0.2
    }

    private var heartbeatJob: Job? = null

    /** Consecutive heartbeat cycles with no received frames. */
    private var consecutiveMisses = 0

    /** Callback when a dead connection is detected. */
    var onDeadConnection: (() -> Unit)? = null

    /** Start the periodic heartbeat loop. */
    fun start() {
        stop()
        consecutiveMisses = 0
        heartbeatJob = scope.launch {
            runHeartbeatLoop()
        }
        AppLogger.i(TAG, "Heartbeat started (interval=${intervalSeconds}s)")
    }

    /** Stop the periodic heartbeat loop. */
    fun stop() {
        heartbeatJob?.cancel()
        heartbeatJob = null
    }

    private suspend fun runHeartbeatLoop() {
        val intervalMs = intervalSeconds * 1_000L

        while (currentCoroutineContext().isActive) {
            delay(intervalMs)

            if (!tcpClient.isConnected) {
                AppLogger.d(TAG, "TCP not connected, skipping heartbeat")
                consecutiveMisses = 0
                continue
            }

            // Exponential backoff: multiplier grows with consecutive misses, capped
            val multiplier = (INITIAL_DEAD_MULTIPLIER + consecutiveMisses)
                .coerceAtMost(MAX_DEAD_MULTIPLIER)
            val baseThresholdMs = intervalMs * multiplier
            // Add jitter to avoid false reconnects on slow networks
            val jitterMs = (baseThresholdMs * JITTER_FRACTION * Math.random()).toLong()
            val deadThresholdMs = baseThresholdMs + jitterMs

            // Check for dead connection
            val timeSinceLastReceived = System.currentTimeMillis() - tcpClient.lastReceivedTimestamp
            if (timeSinceLastReceived > deadThresholdMs) {
                AppLogger.w(
                    TAG,
                    "Dead connection detected: no frames received for ${timeSinceLastReceived}ms " +
                        "(threshold=${deadThresholdMs}ms, multiplier=${multiplier}x, misses=$consecutiveMisses)"
                )
                consecutiveMisses = 0
                onDeadConnection?.invoke()
                return // Stop heartbeat — reconnect logic will restart it
            }

            // Track consecutive misses for backoff
            if (timeSinceLastReceived > intervalMs * INITIAL_DEAD_MULTIPLIER) {
                consecutiveMisses++
                AppLogger.d(TAG, "No frames for ${timeSinceLastReceived}ms, consecutive misses: $consecutiveMisses")
            } else {
                consecutiveMisses = 0
            }

            // Send heartbeat
            try {
                tcpClient.sendHeartbeat()
                AppLogger.d(TAG, "Heartbeat sent")
            } catch (e: Exception) {
                AppLogger.w(TAG, "Heartbeat send failed: ${e.message}")
            }
        }
    }
}

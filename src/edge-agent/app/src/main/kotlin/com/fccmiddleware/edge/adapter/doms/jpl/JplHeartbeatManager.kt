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
        /** Dead connection detected after 3× heartbeat interval with no received frames. */
        private const val DEAD_CONNECTION_MULTIPLIER = 3
    }

    private var heartbeatJob: Job? = null

    /** Callback when a dead connection is detected. */
    var onDeadConnection: (() -> Unit)? = null

    /** Start the periodic heartbeat loop. */
    fun start() {
        stop()
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
        val deadThresholdMs = intervalMs * DEAD_CONNECTION_MULTIPLIER

        while (currentCoroutineContext().isActive) {
            delay(intervalMs)

            if (!tcpClient.isConnected) {
                AppLogger.d(TAG, "TCP not connected, skipping heartbeat")
                continue
            }

            // Check for dead connection
            val timeSinceLastReceived = System.currentTimeMillis() - tcpClient.lastReceivedTimestamp
            if (timeSinceLastReceived > deadThresholdMs) {
                AppLogger.w(
                    TAG,
                    "Dead connection detected: no frames received for ${timeSinceLastReceived}ms " +
                        "(threshold=${deadThresholdMs}ms)"
                )
                onDeadConnection?.invoke()
                return // Stop heartbeat — reconnect logic will restart it
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

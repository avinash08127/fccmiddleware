package com.fccmiddleware.edge.adapter.doms.jpl

import android.util.Log
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.min

/**
 * Persistent TCP client for the DOMS JPL binary protocol.
 *
 * Features:
 *   - Coroutine-based read loop for incoming frames
 *   - Request-response correlation via CompletableDeferred keyed by message name
 *   - Automatic reconnection with exponential backoff
 *   - Thread-safe send via Mutex
 *   - Unsolicited message callback for push notifications
 *
 * Lifecycle: create → connect() → send/receive → disconnect()
 */
class JplTcpClient(
    private val host: String,
    private val port: Int,
    private val scope: CoroutineScope,
    private val connectTimeoutMs: Long = 10_000L,
    private val responseTimeoutMs: Long = 15_000L,
) {
    companion object {
        private const val TAG = "JplTcpClient"
        private const val READ_BUFFER_SIZE = 8192
    }

    private var socket: Socket? = null
    private var inputStream: InputStream? = null
    private var outputStream: OutputStream? = null
    private var readLoopJob: Job? = null

    private val connected = AtomicBoolean(false)
    private val sendMutex = Mutex()
    private val lastReceivedAt = AtomicLong(0L)

    /**
     * Pending request-response correlations.
     * Keyed by expected response message name.
     */
    private val pendingResponses = ConcurrentHashMap<String, CompletableDeferred<JplMessage>>()

    /** Callback for unsolicited (push) messages that don't match any pending request. */
    var onUnsolicitedMessage: ((JplMessage) -> Unit)? = null

    /** Callback when connection is unexpectedly lost. */
    var onDisconnected: ((reason: String) -> Unit)? = null

    /** Callback when a heartbeat response is received. */
    var onHeartbeatReceived: (() -> Unit)? = null

    /** Whether the TCP connection is currently alive. */
    val isConnected: Boolean get() = connected.get()

    /** Timestamp (epoch millis) of the last received frame. */
    val lastReceivedTimestamp: Long get() = lastReceivedAt.get()

    /**
     * Establish the TCP connection and start the read loop.
     *
     * @throws java.io.IOException if connection fails.
     */
    suspend fun connect() = withContext(Dispatchers.IO) {
        disconnect() // Clean up any previous connection

        val sock = Socket()
        sock.connect(InetSocketAddress(host, port), connectTimeoutMs.toInt())
        sock.soTimeout = 0 // Non-blocking read loop uses coroutine cancellation
        sock.tcpNoDelay = true

        socket = sock
        inputStream = sock.getInputStream()
        outputStream = sock.getOutputStream()
        connected.set(true)
        lastReceivedAt.set(System.currentTimeMillis())

        Log.i(TAG, "Connected to $host:$port")

        // Start read loop
        readLoopJob = scope.launch(Dispatchers.IO) { readLoop() }
    }

    /**
     * Gracefully close the TCP connection.
     * Idempotent — safe to call when already disconnected.
     */
    suspend fun disconnect() {
        readLoopJob?.cancel()
        readLoopJob = null

        withContext(Dispatchers.IO) {
            try {
                socket?.close()
            } catch (_: Exception) { }
        }

        socket = null
        inputStream = null
        outputStream = null
        connected.set(false)

        // Fail all pending requests
        pendingResponses.values.forEach { deferred ->
            deferred.completeExceptionally(
                java.io.IOException("Connection closed")
            )
        }
        pendingResponses.clear()
    }

    /**
     * Send a JPL message and wait for a correlated response.
     *
     * @param message The message to send.
     * @param expectedResponseName The JPL message name expected in the response.
     * @return The response message.
     * @throws java.io.IOException if send fails or connection is lost.
     * @throws kotlinx.coroutines.TimeoutCancellationException if response times out.
     */
    suspend fun sendAndReceive(message: JplMessage, expectedResponseName: String): JplMessage {
        val json = kotlinx.serialization.json.Json.encodeToString(
            JplMessage.serializer(), message
        )
        val frame = JplFrameCodec.encode(json)

        val deferred = CompletableDeferred<JplMessage>()
        pendingResponses[expectedResponseName] = deferred

        try {
            sendMutex.withLock {
                withContext(Dispatchers.IO) {
                    val os = outputStream ?: throw java.io.IOException("Not connected")
                    os.write(frame)
                    os.flush()
                }
            }

            return withTimeout(responseTimeoutMs) { deferred.await() }
        } catch (e: Exception) {
            pendingResponses.remove(expectedResponseName)
            throw e
        }
    }

    /**
     * Send a heartbeat frame (no response expected — response handled via callback).
     */
    suspend fun sendHeartbeat() {
        val frame = JplFrameCodec.encodeHeartbeat()

        sendMutex.withLock {
            withContext(Dispatchers.IO) {
                val os = outputStream ?: throw java.io.IOException("Not connected")
                os.write(frame)
                os.flush()
            }
        }
    }

    // ── Read Loop ────────────────────────────────────────────────────────────

    private suspend fun readLoop() {
        val buffer = ByteArray(READ_BUFFER_SIZE)
        var accumulated = ByteArray(0)

        try {
            while (currentCoroutineContext().isActive && connected.get()) {
                val bytesRead = withContext(Dispatchers.IO) {
                    inputStream?.read(buffer) ?: -1
                }

                if (bytesRead == -1) {
                    Log.w(TAG, "TCP read returned -1 (EOF)")
                    break
                }

                if (bytesRead > 0) {
                    accumulated = accumulated + buffer.copyOfRange(0, bytesRead)
                    accumulated = processAccumulated(accumulated)
                }
            }
        } catch (e: CancellationException) {
            throw e // Propagate cancellation
        } catch (e: Exception) {
            Log.e(TAG, "Read loop error: ${e.message}")
        }

        if (connected.getAndSet(false)) {
            Log.w(TAG, "Connection lost")
            pendingResponses.values.forEach { it.completeExceptionally(java.io.IOException("Connection lost")) }
            pendingResponses.clear()
            onDisconnected?.invoke("TCP read loop ended")
        }
    }

    /**
     * Process accumulated bytes, extracting complete frames.
     * Returns the remaining unprocessed bytes.
     */
    private fun processAccumulated(data: ByteArray): ByteArray {
        var remaining = data

        while (remaining.isNotEmpty()) {
            val result = JplFrameCodec.decode(remaining)

            when (result) {
                is DecodeResult.Frame -> {
                    lastReceivedAt.set(System.currentTimeMillis())
                    handleFrame(result.payload)
                    remaining = remaining.copyOfRange(
                        result.bytesConsumed, remaining.size
                    )
                }
                is DecodeResult.Heartbeat -> {
                    lastReceivedAt.set(System.currentTimeMillis())
                    onHeartbeatReceived?.invoke()
                    remaining = remaining.copyOfRange(
                        result.bytesConsumed, remaining.size
                    )
                }
                is DecodeResult.Incomplete -> {
                    return remaining // Wait for more data
                }
                is DecodeResult.Error -> {
                    Log.w(TAG, "Frame decode error: ${result.message}")
                    remaining = remaining.copyOfRange(
                        min(result.bytesConsumed.coerceAtLeast(1), remaining.size),
                        remaining.size
                    )
                }
            }
        }

        return ByteArray(0)
    }

    /**
     * Handle a decoded JSON frame — dispatch to pending request or unsolicited callback.
     */
    private fun handleFrame(json: String) {
        try {
            val message = kotlinx.serialization.json.Json.decodeFromString(
                JplMessage.serializer(), json
            )

            // Check if this matches a pending request
            val deferred = pendingResponses.remove(message.name)
            if (deferred != null) {
                deferred.complete(message)
            } else {
                // Unsolicited message (push notification from FCC)
                onUnsolicitedMessage?.invoke(message)
            }
        } catch (e: Exception) {
            Log.e(TAG, "Failed to parse JPL message: ${e.message}")
        }
    }
}

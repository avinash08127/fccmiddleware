package com.fccmiddleware.edge.adapter.common

/**
 * Optional lifecycle interface for FCC adapters that maintain persistent connections.
 *
 * Implemented by DOMS TCP/JPL adapter (persistent TCP socket with heartbeat).
 * NOT implemented by Radix (stateless HTTP) or Petronite (stateless REST + webhook).
 *
 * CadenceController checks `adapter is IFccConnectionLifecycle` at runtime:
 *   - If true: calls connect() on startup, disconnect() on shutdown, wires event listener
 *   - If false: skips lifecycle management entirely (adapter handles its own connections)
 */
interface IFccConnectionLifecycle {

    /**
     * Establish the persistent connection to the FCC.
     *
     * For DOMS: opens TCP socket, completes FcLogon handshake, starts heartbeat timer.
     * Must be called before any IFccAdapter operations (fetch, pre-auth, etc.).
     *
     * @throws FccConnectionException if the connection cannot be established.
     */
    suspend fun connect()

    /**
     * Gracefully close the persistent connection.
     *
     * Stops heartbeat, sends disconnect message if protocol supports it, closes socket.
     * Idempotent — safe to call when already disconnected.
     */
    suspend fun disconnect()

    /**
     * Check whether the persistent connection is currently alive and authenticated.
     *
     * For DOMS: true if TCP socket is open AND FcLogon was successful AND
     * last heartbeat response was received within 3x heartbeat interval.
     */
    val isConnected: Boolean

    /**
     * Register a callback listener for unsolicited FCC events.
     *
     * The listener receives push notifications (pump status changes, transaction
     * availability, fuelling updates) from the FCC without polling.
     * Set to null to unregister.
     */
    fun setEventListener(listener: IFccEventListener?)
}

/**
 * Thrown when a persistent FCC connection cannot be established or is unexpectedly lost.
 */
class FccConnectionException(message: String, cause: Throwable? = null) : Exception(message, cause)

package com.fccmiddleware.edge.connectivity

import android.net.Network
import com.fccmiddleware.edge.logging.AppLogger
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import javax.net.SocketFactory

/**
 * OkHttp [SocketFactory] that binds sockets to an Android [Network].
 *
 * When a network is available from [networkProvider], newly created sockets are
 * bound to that network via [Network.bindSocket]. When no network is available,
 * sockets use default OS routing.
 *
 * Used to route cloud HTTP traffic preferentially over mobile data (with WiFi fallback).
 * The binding is per-socket — it does not affect other apps or the local API server.
 */
class BoundSocketFactory(
    private val networkProvider: () -> Network?,
) : SocketFactory() {

    companion object {
        private const val TAG = "BoundSocketFactory"
    }

    override fun createSocket(): Socket {
        val socket = Socket()
        bindIfAvailable(socket)
        return socket
    }

    override fun createSocket(host: String, port: Int): Socket {
        val socket = createSocket()
        socket.connect(InetSocketAddress(host, port))
        return socket
    }

    override fun createSocket(
        host: String,
        port: Int,
        localHost: InetAddress,
        localPort: Int,
    ): Socket {
        val socket = createSocket()
        socket.bind(InetSocketAddress(localHost, localPort))
        socket.connect(InetSocketAddress(host, port))
        return socket
    }

    override fun createSocket(host: InetAddress, port: Int): Socket {
        val socket = createSocket()
        socket.connect(InetSocketAddress(host, port))
        return socket
    }

    override fun createSocket(
        address: InetAddress,
        port: Int,
        localAddress: InetAddress,
        localPort: Int,
    ): Socket {
        val socket = createSocket()
        socket.bind(InetSocketAddress(localAddress, localPort))
        socket.connect(InetSocketAddress(address, port))
        return socket
    }

    private fun bindIfAvailable(socket: Socket) {
        val network = networkProvider()
        if (network != null) {
            try {
                network.bindSocket(socket)
            } catch (e: Exception) {
                AppLogger.w(TAG, "Failed to bind socket to network $network: ${e.message}")
            }
        }
    }
}

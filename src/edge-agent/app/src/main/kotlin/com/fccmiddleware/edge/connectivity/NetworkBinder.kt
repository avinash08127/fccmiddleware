package com.fccmiddleware.edge.connectivity

import android.content.Context
import android.net.ConnectivityManager as AndroidConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import com.fccmiddleware.edge.logging.AppLogger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn

/**
 * NetworkBinder — tracks WiFi and mobile data networks as reactive state flows.
 *
 * Uses Android ConnectivityManager.registerNetworkCallback() to monitor:
 * - WiFi network (TRANSPORT_WIFI) — used for FCC communication over station LAN
 * - Mobile data network (TRANSPORT_CELLULAR + NET_CAPABILITY_INTERNET) — preferred for cloud traffic
 *
 * Exposes:
 * - [wifiNetwork]: non-null when WiFi is connected, null when lost
 * - [mobileNetwork]: non-null when cellular with internet is available, null when lost
 * - [cloudNetwork]: preferred network for cloud traffic (mobile > WiFi > null)
 */
class NetworkBinder(
    private val context: Context,
    private val scope: CoroutineScope,
) {
    private val cm = context.getSystemService(AndroidConnectivityManager::class.java)

    private val _wifiNetwork = MutableStateFlow<Network?>(null)
    val wifiNetwork: StateFlow<Network?> = _wifiNetwork

    private val _mobileNetwork = MutableStateFlow<Network?>(null)
    val mobileNetwork: StateFlow<Network?> = _mobileNetwork

    /** The preferred network for cloud traffic: mobile > WiFi > null */
    val cloudNetwork: StateFlow<Network?> = combine(_mobileNetwork, _wifiNetwork) { mobile, wifi ->
        mobile ?: wifi
    }.stateIn(scope, SharingStarted.Eagerly, null)

    private var started = false

    companion object {
        private const val TAG = "NetworkBinder"
    }

    private val wifiCallback = object : AndroidConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            AppLogger.i(TAG, "WiFi network available: $network")
            _wifiNetwork.value = network
        }

        override fun onLost(network: Network) {
            AppLogger.i(TAG, "WiFi network lost: $network")
            _wifiNetwork.value = null
        }
    }

    private val mobileCallback = object : AndroidConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            AppLogger.i(TAG, "Mobile network available: $network")
            _mobileNetwork.value = network
        }

        override fun onLost(network: Network) {
            AppLogger.i(TAG, "Mobile network lost: $network")
            _mobileNetwork.value = null
        }
    }

    /**
     * Registers network callbacks for WiFi and mobile data monitoring.
     * Safe to call multiple times — duplicate calls are ignored.
     */
    fun start() {
        if (started) {
            AppLogger.d(TAG, "Already started, ignoring duplicate start()")
            return
        }

        val wifiRequest = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .build()

        val mobileRequest = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_CELLULAR)
            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .build()

        cm.registerNetworkCallback(wifiRequest, wifiCallback)
        cm.registerNetworkCallback(mobileRequest, mobileCallback)
        started = true

        AppLogger.i(TAG, "NetworkBinder started — monitoring WiFi and mobile networks")
    }

    /**
     * Unregisters all network callbacks and resets state flows to null.
     * Safe to call multiple times — duplicate calls are ignored.
     */
    fun stop() {
        if (!started) {
            AppLogger.d(TAG, "Not started, ignoring duplicate stop()")
            return
        }

        try {
            cm.unregisterNetworkCallback(wifiCallback)
        } catch (e: IllegalArgumentException) {
            AppLogger.w(TAG, "WiFi callback already unregistered: ${e.message}")
        }

        try {
            cm.unregisterNetworkCallback(mobileCallback)
        } catch (e: IllegalArgumentException) {
            AppLogger.w(TAG, "Mobile callback already unregistered: ${e.message}")
        }

        _wifiNetwork.value = null
        _mobileNetwork.value = null
        started = false

        AppLogger.i(TAG, "NetworkBinder stopped")
    }
}

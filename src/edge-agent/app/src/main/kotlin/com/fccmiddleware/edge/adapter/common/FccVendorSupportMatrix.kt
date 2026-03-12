package com.fccmiddleware.edge.adapter.common

/**
 * Published FCC support matrix for the Android runtime.
 */
object FccVendorSupportMatrix {

    fun isSupported(vendor: FccVendor, connectionProtocol: String): Boolean =
        when (vendor) {
            FccVendor.DOMS -> connectionProtocol.equals("TCP", ignoreCase = true)
            FccVendor.RADIX,
            FccVendor.PETRONITE,
            -> true
            FccVendor.ADVATEC -> true
        }

    fun describe(vendor: FccVendor): String =
        when (vendor) {
            FccVendor.DOMS -> "TCP/JPL only"
            FccVendor.RADIX -> "supported"
            FccVendor.PETRONITE -> "supported"
            FccVendor.ADVATEC -> "supported"
        }

    fun unsupportedMessage(vendor: FccVendor, connectionProtocol: String): String =
        "Vendor $vendor with protocol $connectionProtocol is not supported on Android. " +
            "Published support: ${describe(vendor)}."
}

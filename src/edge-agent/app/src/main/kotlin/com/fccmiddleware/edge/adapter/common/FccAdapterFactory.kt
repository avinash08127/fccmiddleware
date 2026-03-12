package com.fccmiddleware.edge.adapter.common

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.advatec.AdvatecAdapter
import com.fccmiddleware.edge.adapter.doms.DomsAdapter
import com.fccmiddleware.edge.adapter.petronite.PetroniteAdapter
import com.fccmiddleware.edge.adapter.radix.RadixAdapter
import com.fccmiddleware.edge.connectivity.NetworkBinder

/**
 * Default [IFccAdapterFactory] implementation.
 *
 * Resolves vendor adapters at runtime based on [FccVendor]. Throws
 * [AdapterNotRegisteredException] when no binding exists for the requested vendor,
 * and [AdapterNotImplementedException] when a binding exists but the adapter is
 * still a stub (all methods throw [UnsupportedOperationException]).
 *
 * Per §5.4 of the FCC Adapter Interface Contracts spec, no fallback adapter is permitted.
 */
class FccAdapterFactory(
    private val networkBinder: NetworkBinder? = null,
) : IFccAdapterFactory {

    companion object {
        private const val TAG = "FccAdapterFactory"
    }

    override fun resolve(vendor: FccVendor, config: AgentFccConfig): IFccAdapter {
        if (!FccVendorSupportMatrix.isSupported(vendor, config.connectionProtocol)) {
            AppLogger.e(
                TAG,
                FccVendorSupportMatrix.unsupportedMessage(vendor, config.connectionProtocol),
            )
            throw AdapterNotImplementedException(vendor, config.connectionProtocol)
        }

        return when (vendor) {
            FccVendor.DOMS -> com.fccmiddleware.edge.adapter.doms.DomsJplAdapter(
                config,
                socketBinder = networkBinder?.let { binder ->
                    { socket ->
                        binder.wifiNetwork.value?.bindSocket(socket)
                    }
                },
            )
            FccVendor.RADIX -> RadixAdapter(config)
            FccVendor.PETRONITE -> PetroniteAdapter(config)
            FccVendor.ADVATEC -> AdvatecAdapter(config)
            else -> throw AdapterNotRegisteredException(vendor)
        }
    }
}

/**
 * Thrown when an adapter binding exists for the vendor but the implementation
 * is not yet complete (all methods are stubs).
 */
class AdapterNotImplementedException(vendor: FccVendor, connectionProtocol: String) : Exception(
    FccVendorSupportMatrix.unsupportedMessage(vendor, connectionProtocol) +
        " Error code: ADAPTER_NOT_IMPLEMENTED",
)

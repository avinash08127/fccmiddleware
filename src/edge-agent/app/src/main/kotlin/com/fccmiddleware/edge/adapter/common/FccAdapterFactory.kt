package com.fccmiddleware.edge.adapter.common

import android.util.Log
import com.fccmiddleware.edge.adapter.doms.DomsAdapter
import com.fccmiddleware.edge.adapter.petronite.PetroniteAdapter
import com.fccmiddleware.edge.adapter.radix.RadixAdapter

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
class FccAdapterFactory : IFccAdapterFactory {

    companion object {
        private const val TAG = "FccAdapterFactory"
    }

    override fun resolve(vendor: FccVendor, config: AgentFccConfig): IFccAdapter {
        if (!FccVendorSupportMatrix.isSupported(vendor, config.connectionProtocol)) {
            Log.e(
                TAG,
                FccVendorSupportMatrix.unsupportedMessage(vendor, config.connectionProtocol),
            )
            throw AdapterNotImplementedException(vendor, config.connectionProtocol)
        }

        return when (vendor) {
            FccVendor.DOMS -> com.fccmiddleware.edge.adapter.doms.DomsJplAdapter(config)
            FccVendor.RADIX -> RadixAdapter(config)
            FccVendor.PETRONITE -> PetroniteAdapter(config)
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

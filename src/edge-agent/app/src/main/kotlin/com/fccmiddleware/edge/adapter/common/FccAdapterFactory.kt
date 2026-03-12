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

        /** Vendors whose adapters are fully implemented and safe to use. */
        private val IMPLEMENTED_VENDORS: Set<FccVendor> = setOf(
            // Add vendors here as their adapters are completed.
            // e.g. FccVendor.RADIX once RadixAdapter is implemented.
        )
    }

    override fun resolve(vendor: FccVendor, config: AgentFccConfig): IFccAdapter {
        if (vendor !in IMPLEMENTED_VENDORS) {
            Log.e(
                TAG,
                "Adapter for vendor $vendor is not implemented. " +
                    "Implemented vendors: $IMPLEMENTED_VENDORS",
            )
            throw AdapterNotImplementedException(vendor)
        }

        return when (vendor) {
            FccVendor.DOMS -> DomsAdapter(config)
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
class AdapterNotImplementedException(vendor: FccVendor) : Exception(
    "Adapter for vendor $vendor exists but is not yet implemented. " +
        "Error code: ADAPTER_NOT_IMPLEMENTED",
)

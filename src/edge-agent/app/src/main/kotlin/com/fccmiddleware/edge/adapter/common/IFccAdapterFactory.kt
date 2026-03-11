package com.fccmiddleware.edge.adapter.common

/**
 * Factory interface for resolving FCC vendor adapters at runtime.
 *
 * Resolution is config-driven by FccVendor. One adapter binding per vendor.
 * Resolve must throw AdapterNotRegisteredException (error code ADAPTER_NOT_REGISTERED)
 * when no binding exists for the requested vendor — no fallback adapter is permitted.
 *
 * Contract defined in docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md §5.4.
 */
interface IFccAdapterFactory {

    /**
     * Return the registered IFccAdapter for the given vendor, configured with the
     * supplied site config.
     *
     * @throws AdapterNotRegisteredException if vendor has no registered binding.
     */
    fun resolve(vendor: FccVendor, config: AgentFccConfig): IFccAdapter
}

/** Thrown when no adapter binding exists for the requested FccVendor. */
class AdapterNotRegisteredException(vendor: FccVendor) : Exception(
    "No adapter registered for vendor $vendor. Error code: ADAPTER_NOT_REGISTERED"
)

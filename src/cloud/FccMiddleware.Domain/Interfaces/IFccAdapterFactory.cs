using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Models.Adapter;

namespace FccMiddleware.Domain.Interfaces;

/// <summary>
/// Resolves the correct IFccAdapter for a given vendor and site configuration.
/// One adapter binding per FccVendor is permitted. Resolution fails fast with
/// AdapterNotRegisteredException when an active configured vendor has no registered binding.
///
/// Matches the factory contract in §5.4 of
/// docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md.
/// </summary>
public interface IFccAdapterFactory
{
    /// <summary>
    /// Returns a configured IFccAdapter for the given vendor and site config.
    /// Throws <see cref="Exceptions.AdapterNotRegisteredException"/> when no adapter
    /// is registered for the vendor. No fallback adapter is permitted.
    /// </summary>
    IFccAdapter Resolve(FccVendor vendor, SiteFccConfig config);
}

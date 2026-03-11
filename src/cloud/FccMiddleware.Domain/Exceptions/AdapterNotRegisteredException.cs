using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Domain.Exceptions;

/// <summary>
/// Thrown by IFccAdapterFactory.Resolve when no adapter is registered for the requested vendor.
/// Error code: ADAPTER_NOT_REGISTERED.
/// No fallback adapter is permitted per §5.4 of the adapter interface contracts spec.
/// </summary>
public sealed class AdapterNotRegisteredException : Exception
{
    public const string ErrorCode = "ADAPTER_NOT_REGISTERED";

    public FccVendor Vendor { get; }

    public AdapterNotRegisteredException(FccVendor vendor)
        : base($"{ErrorCode}: No adapter registered for vendor '{vendor}'.")
    {
        Vendor = vendor;
    }
}

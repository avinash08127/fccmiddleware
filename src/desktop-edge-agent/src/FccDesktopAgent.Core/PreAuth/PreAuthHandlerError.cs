namespace FccDesktopAgent.Core.PreAuth;

/// <summary>
/// Structured error codes returned by <see cref="IPreAuthHandler"/> on rejection.
/// Mapped to HTTP error responses by the local API controller.
/// </summary>
public enum PreAuthHandlerError
{
    /// <summary>No NozzleMapping found for the given site + Odoo pump/nozzle numbers.</summary>
    NozzleMappingNotFound,

    /// <summary>The resolved nozzle is marked inactive in the local NozzleMapping table.</summary>
    NozzleInactive,

    /// <summary>FCC is currently unreachable (state is FccUnreachable or FullyOffline).</summary>
    FccUnreachable,

    /// <summary>FCC adapter factory has not been configured — agent is not fully provisioned.</summary>
    AdapterNotConfigured,

    /// <summary>FCC explicitly declined the pre-auth request.</summary>
    FccDeclined,

    /// <summary>FCC call timed out without responding.</summary>
    FccTimeout,

    /// <summary>No pre-auth record found for the given OdooOrderId (used by cancel).</summary>
    RecordNotFound,

    /// <summary>Cannot cancel a pre-auth whose pump is actively dispensing.</summary>
    CannotCancelDispensing,
}

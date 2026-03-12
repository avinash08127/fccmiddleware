namespace FccDesktopAgent.Core.PreAuth;

/// <summary>
/// Handles pre-authorization commands from Odoo POS (HHTs) over the local LAN.
/// Pre-auth is always relayed to the FCC over LAN — never via cloud.
/// Architecture rule #11: POST /api/preauth must respond based on LAN-only work.
/// </summary>
public interface IPreAuthHandler
{
    /// <summary>
    /// Process a pre-auth request from Odoo POS.
    /// Performs local dedup, nozzle mapping, FCC forwarding, and local record creation.
    /// p95 local overhead target: &lt;= 50 ms (excluding FCC call time).
    /// Cloud forwarding is always asynchronous and never blocks this call.
    /// </summary>
    Task<PreAuthHandlerResult> HandleAsync(OdooPreAuthRequest request, CancellationToken ct);

    /// <summary>
    /// Cancel a pending or authorized pre-auth identified by <paramref name="odooOrderId"/>.
    /// Attempts best-effort FCC deauthorization before updating the local record.
    /// Returns an error result if the pre-auth is actively dispensing or not found.
    /// </summary>
    Task<PreAuthHandlerResult> CancelAsync(string odooOrderId, string siteCode, CancellationToken ct);

    /// <summary>
    /// Query pre-auths past their <c>ExpiresAt</c> that are still in a non-terminal status
    /// and transition them to <see cref="Adapter.Common.PreAuthStatus.Expired"/>.
    /// Attempts best-effort FCC deauthorization for each.
    /// Returns the number of records expired.
    /// Called periodically by <c>CadenceController</c>.
    /// </summary>
    Task<int> RunExpiryCheckAsync(CancellationToken ct);
}

using FccDesktopAgent.Core.Adapter.Common;

namespace FccDesktopAgent.Core.PreAuth;

/// <summary>
/// Handles pre-authorization commands from Odoo POS (HHTs) over the local LAN.
/// Pre-auth is always relayed to the FCC over LAN — never via cloud.
/// Architecture rule #11: POST /api/preauth must respond based on LAN-only work.
/// </summary>
public interface IPreAuthHandler
{
    /// <summary>
    /// Forward a pre-auth command to the FCC and return the result.
    /// p95 local overhead target: &lt;= 50ms (excluding FCC call time).
    /// </summary>
    Task<PreAuthResult> HandleAsync(PreAuthCommand command, CancellationToken ct);
}

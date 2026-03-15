namespace FccDesktopAgent.Core.Security;

/// <summary>
/// S-DSK-033: Allows the configuration layer to signal that the LAN API key
/// has been rotated so the running API stack picks up the new key immediately
/// without requiring an agent restart.
/// </summary>
public interface IApiKeyRefresher
{
    /// <summary>Reload the LAN API key from the credential store.</summary>
    Task RefreshKeyAsync(CancellationToken ct = default);
}

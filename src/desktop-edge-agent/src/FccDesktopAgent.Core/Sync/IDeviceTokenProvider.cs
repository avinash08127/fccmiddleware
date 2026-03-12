namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Provides the device JWT used to authenticate cloud API requests.
/// The token is stored in the platform credential store and refreshed on 401.
/// </summary>
public interface IDeviceTokenProvider
{
    /// <summary>Returns the current stored device JWT. Null if not yet registered.</summary>
    Task<string?> GetTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Refreshes the device JWT via POST /api/v1/agent/token/refresh.
    /// Sends the refresh token in the request body (token rotation per spec).
    /// Updates the credential store with both the new device token and new refresh token.
    /// Returns the new device token, or null if refresh failed.
    /// Throws <see cref="DeviceDecommissionedException"/> on 403 DEVICE_DECOMMISSIONED.
    /// </summary>
    Task<string?> RefreshTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores both the device JWT and refresh token in the credential store.
    /// Called by the registration service after initial registration and by refresh on rotation.
    /// </summary>
    Task StoreTokensAsync(string deviceToken, string refreshToken, CancellationToken ct = default);
}

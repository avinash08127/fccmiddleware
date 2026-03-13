namespace FccDesktopAgent.Core.Security;

/// <summary>
/// Well-known credential store key names for secrets managed by the Desktop Edge Agent.
/// All keys follow the pattern "category:name" to avoid collisions.
/// </summary>
public static class CredentialKeys
{
    /// <summary>Device JWT access token for cloud API calls.</summary>
    public const string DeviceToken = "device:token";

    /// <summary>Refresh token for device JWT rotation.</summary>
    public const string RefreshToken = "device:refresh_token";

    /// <summary>Atomic device credential bundle containing both the JWT and refresh token.</summary>
    public const string DeviceTokenBundle = "device:token_bundle";

    /// <summary>Staging entry used to recover from interrupted token bundle commits.</summary>
    public const string DeviceTokenBundleStaging = "device:token_bundle:staging";

    /// <summary>Marker written before a token refresh call so interrupted refreshes can be detected safely.</summary>
    public const string DeviceTokenRefreshPending = "device:token_refresh_pending";

    /// <summary>FCC API key for LAN authentication to the Forecourt Controller.</summary>
    public const string FccApiKey = "fcc:api_key";

    /// <summary>LAN API key used by Odoo POS / HHTs to authenticate against the local REST API.</summary>
    public const string LanApiKey = "lan:api_key";
}

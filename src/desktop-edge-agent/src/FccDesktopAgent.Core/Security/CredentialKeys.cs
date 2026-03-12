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

    /// <summary>FCC API key for LAN authentication to the Forecourt Controller.</summary>
    public const string FccApiKey = "fcc:api_key";

    /// <summary>LAN API key used by Odoo POS / HHTs to authenticate against the local REST API.</summary>
    public const string LanApiKey = "lan:api_key";
}

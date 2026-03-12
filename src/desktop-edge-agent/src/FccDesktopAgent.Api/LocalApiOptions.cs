using FccDesktopAgent.Core.Security;

namespace FccDesktopAgent.Api;

/// <summary>
/// Configuration options for the embedded Kestrel local REST API.
/// Bound from the "LocalApi" configuration section.
/// </summary>
public sealed class LocalApiOptions
{
    public const string SectionName = "LocalApi";

    /// <summary>Port Kestrel listens on. Default 8585.</summary>
    public int Port { get; set; } = 8585;

    /// <summary>
    /// LAN API key required in the X-Api-Key header for all requests.
    /// Unlike the Android agent, there is no localhost bypass — Odoo POS is always
    /// on a separate HHT. Leave empty to disable auth (development only).
    /// </summary>
    [SensitiveData]
    public string ApiKey { get; set; } = string.Empty;
}

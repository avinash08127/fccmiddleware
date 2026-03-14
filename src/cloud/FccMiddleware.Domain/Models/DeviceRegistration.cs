using FccMiddleware.Domain.Common;

namespace FccMiddleware.Domain.Models;

/// <summary>
/// Compact payload encoded in the provisioning QR code.
/// Field names are abbreviated to minimize QR code size.
/// </summary>
public class QrCodePayload
{
    /// <summary>QR payload schema version (always 1).</summary>
    public int V { get; set; } = 1;

    /// <summary>Site code.</summary>
    public string Sc { get; set; } = null!;

    /// <summary>Cloud base URL.</summary>
    public string Cu { get; set; } = null!;

    /// <summary>Provisioning token (base64url-encoded, 32 bytes).</summary>
    [Sensitive]
    public string Pt { get; set; } = null!;
}

/// <summary>
/// Sent by the Edge Agent to POST /api/v1/agent/register to bootstrap registration.
/// Matches DeviceRegistrationRequest in device-registration.schema.json.
/// </summary>
public class DeviceRegistrationRequest
{
    /// <summary>One-time bootstrap token from QR code or manual entry.</summary>
    [Sensitive]
    public string ProvisioningToken { get; set; } = null!;

    public string SiteCode { get; set; } = null!;

    /// <summary>Android device serial number (Build.SERIAL or ANDROID_ID).</summary>
    public string DeviceSerialNumber { get; set; } = null!;

    /// <summary>Android device model (Build.MODEL).</summary>
    public string DeviceModel { get; set; } = null!;

    /// <summary>Android OS version (Build.VERSION.RELEASE).</summary>
    public string OsVersion { get; set; } = null!;

    /// <summary>Edge Agent app version in semantic version format (e.g., 1.2.3).</summary>
    public string AgentVersion { get; set; } = null!;

    /// <summary>Registration-time device class. Expected values: ANDROID, DESKTOP.</summary>
    public string DeviceClass { get; set; } = "ANDROID";

    /// <summary>HA role eligibility advertised by the registering device.</summary>
    public string? RoleCapability { get; set; }

    /// <summary>Configured HA priority where lower numbers have higher preference.</summary>
    public int? SiteHaPriority { get; set; }

    /// <summary>Capabilities advertised by the device, such as FCC_CONTROL or LAN_PROXY.</summary>
    public string[] Capabilities { get; set; } = [];

    /// <summary>Optional peer API metadata used for peer directory bootstrap.</summary>
    public PeerApiRegistrationMetadata? PeerApi { get; set; }

    /// <summary>If true, deactivates any existing active agent for this site with a different serial number.</summary>
    public bool ReplacePreviousAgent { get; set; } = false;
}

public class PeerApiRegistrationMetadata
{
    public string? BaseUrl { get; set; }
    public string? AdvertisedHost { get; set; }
    public int? Port { get; set; }
    public bool TlsEnabled { get; set; }
}

/// <summary>
/// Returned by the cloud on successful registration (HTTP 201).
/// Matches DeviceRegistrationResponse in device-registration.schema.json.
/// </summary>
public class DeviceRegistrationResponse
{
    /// <summary>Unique device identity assigned by the cloud (same as agent_registrations.id).</summary>
    public Guid DeviceId { get; set; }

    /// <summary>Signed JWT for authenticating subsequent API calls. ES256.</summary>
    [Sensitive]
    public string DeviceToken { get; set; } = null!;

    public DateTimeOffset TokenExpiresAt { get; set; }
    public string SiteCode { get; set; } = null!;
    public Guid LegalEntityId { get; set; }

    /// <summary>Full site configuration object returned at registration time.</summary>
    public object SiteConfig { get; set; } = null!;

    public DateTimeOffset RegisteredAt { get; set; }
}

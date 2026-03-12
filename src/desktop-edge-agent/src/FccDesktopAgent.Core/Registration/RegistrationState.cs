namespace FccDesktopAgent.Core.Registration;

/// <summary>
/// Persisted device identity and registration flags.
/// Stored as <c>registration.json</c> in <see cref="Buffer.AgentDataDirectory"/>.
/// Tokens are NOT stored here — they live in <see cref="Security.ICredentialStore"/>.
/// </summary>
public sealed class RegistrationState
{
    public bool IsRegistered { get; set; }
    public bool IsDecommissioned { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string SiteCode { get; set; } = string.Empty;
    public string LegalEntityId { get; set; } = string.Empty;
    public string CloudBaseUrl { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; set; }
    public string DeviceSerialNumber { get; set; } = string.Empty;
    public string DeviceModel { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
}

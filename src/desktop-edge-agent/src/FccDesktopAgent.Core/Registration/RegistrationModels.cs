using System.Text.Json.Serialization;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Security;

namespace FccDesktopAgent.Core.Registration;

/// <summary>
/// Request payload for <c>POST /api/v1/agent/register</c>.
/// </summary>
public sealed class DeviceRegistrationRequest
{
    [JsonPropertyName("provisioningToken")]
    [SensitiveData]
    public required string ProvisioningToken { get; init; }

    [JsonPropertyName("siteCode")]
    public required string SiteCode { get; init; }

    [JsonPropertyName("deviceSerialNumber")]
    public required string DeviceSerialNumber { get; init; }

    [JsonPropertyName("deviceModel")]
    public required string DeviceModel { get; init; }

    [JsonPropertyName("osVersion")]
    public required string OsVersion { get; init; }

    [JsonPropertyName("agentVersion")]
    public required string AgentVersion { get; init; }

    [JsonPropertyName("deviceClass")]
    public string DeviceClass { get; init; } = "DESKTOP";

    [JsonPropertyName("roleCapability")]
    public string? RoleCapability { get; init; } = "PRIMARY_ELIGIBLE";

    [JsonPropertyName("siteHaPriority")]
    public int? SiteHaPriority { get; init; } = 10;

    [JsonPropertyName("capabilities")]
    public string[] Capabilities { get; init; } = ["FCC_CONTROL", "PEER_API", "TRANSACTION_BUFFER", "TELEMETRY"];

    [JsonPropertyName("peerApi")]
    public PeerApiRegistrationMetadata? PeerApi { get; init; }

    [JsonPropertyName("replacePreviousAgent")]
    public bool ReplacePreviousAgent { get; init; }
}

public sealed class PeerApiRegistrationMetadata
{
    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("advertisedHost")]
    public string? AdvertisedHost { get; init; }

    [JsonPropertyName("port")]
    public int? Port { get; init; }

    [JsonPropertyName("tlsEnabled")]
    public bool TlsEnabled { get; init; }
}

/// <summary>
/// Response from <c>POST /api/v1/agent/register</c> (HTTP 201).
/// </summary>
public sealed class DeviceRegistrationResponse
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("deviceToken")]
    [SensitiveData]
    public string DeviceToken { get; init; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    [SensitiveData]
    public string RefreshToken { get; init; } = string.Empty;

    [JsonPropertyName("tokenExpiresAt")]
    public DateTimeOffset TokenExpiresAt { get; init; }

    [JsonPropertyName("siteCode")]
    public string SiteCode { get; init; } = string.Empty;

    [JsonPropertyName("legalEntityId")]
    public string LegalEntityId { get; init; } = string.Empty;

    [JsonPropertyName("siteConfig")]
    public SiteConfig? SiteConfig { get; init; }

    [JsonPropertyName("registeredAt")]
    public DateTimeOffset RegisteredAt { get; init; }
}

/// <summary>
/// Error response body from the registration endpoint (HTTP 4xx).
/// </summary>
public sealed class RegistrationErrorResponse
{
    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>Known registration error codes returned by the cloud.</summary>
public enum RegistrationErrorCode
{
    Unknown,
    BootstrapTokenMissing,
    BootstrapTokenInvalid,
    BootstrapTokenExpired,
    BootstrapTokenRevoked,
    BootstrapTokenAlreadyUsed,
    ActiveAgentExists,
    DevicePendingApproval,
    DeviceQuarantined,
    SiteNotFound,
    SiteMismatch,
}

/// <summary>
/// Result of a device registration attempt. Sealed hierarchy for exhaustive matching.
/// </summary>
public abstract record RegistrationResult
{
    private RegistrationResult() { }

    /// <summary>Registration succeeded. Tokens and identity are already stored.</summary>
    public sealed record Success(DeviceRegistrationResponse Response) : RegistrationResult;

    /// <summary>Cloud rejected the registration with a specific error code.</summary>
    public sealed record Rejected(RegistrationErrorCode Code, string Message) : RegistrationResult;

    /// <summary>Network or transport-level failure.</summary>
    public sealed record TransportError(string Message, Exception? Exception = null) : RegistrationResult;
}

/// <summary>Helper to parse cloud error code strings to <see cref="RegistrationErrorCode"/>.</summary>
internal static class RegistrationErrorCodeParser
{
    internal static RegistrationErrorCode Parse(string? code) => code?.ToUpperInvariant() switch
    {
        "BOOTSTRAP_TOKEN_MISSING" => RegistrationErrorCode.BootstrapTokenMissing,
        "BOOTSTRAP_TOKEN_INVALID" => RegistrationErrorCode.BootstrapTokenInvalid,
        "BOOTSTRAP_TOKEN_EXPIRED" => RegistrationErrorCode.BootstrapTokenExpired,
        "BOOTSTRAP_TOKEN_REVOKED" => RegistrationErrorCode.BootstrapTokenRevoked,
        "BOOTSTRAP_TOKEN_ALREADY_USED" => RegistrationErrorCode.BootstrapTokenAlreadyUsed,
        "ACTIVE_AGENT_EXISTS" => RegistrationErrorCode.ActiveAgentExists,
        "DEVICE_PENDING_APPROVAL" => RegistrationErrorCode.DevicePendingApproval,
        "DEVICE_QUARANTINED" => RegistrationErrorCode.DeviceQuarantined,
        "SITE_NOT_FOUND" => RegistrationErrorCode.SiteNotFound,
        "SITE_MISMATCH" => RegistrationErrorCode.SiteMismatch,
        _ => RegistrationErrorCode.Unknown,
    };
}

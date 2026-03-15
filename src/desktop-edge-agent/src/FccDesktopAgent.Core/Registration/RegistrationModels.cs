using FccMiddleware.Contracts.Common;

namespace FccDesktopAgent.Core.Registration;

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
    ConfigNotFound,
    RegistrationBlocked,
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
        "CONFIG_NOT_FOUND" => RegistrationErrorCode.ConfigNotFound,
        "REGISTRATION_BLOCKED" => RegistrationErrorCode.RegistrationBlocked,
        _ => RegistrationErrorCode.Unknown,
    };
}

internal static class RegistrationErrorExtensions
{
    public static string GetErrorCode(this ErrorResponse? error) =>
        error?.ErrorCode ?? "UNKNOWN";

    public static string GetMessage(this ErrorResponse? error, int statusCode) =>
        error?.Message ?? $"HTTP {statusCode}";
}

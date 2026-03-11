namespace FccMiddleware.Api.Auth;

/// <summary>
/// Configuration for Edge Agent device JWT validation.
/// Bound from appsettings.json section "DeviceJwt".
/// </summary>
public sealed class DeviceJwtOptions
{
    public const string SectionName = "DeviceJwt";

    /// <summary>
    /// HMAC-SHA256 symmetric signing key (base64-encoded or plain string).
    /// In production this is injected from AWS Secrets Manager / environment variables.
    /// </summary>
    public string SigningKey { get; init; } = string.Empty;

    public const string DefaultIssuer   = "fcc-middleware-cloud";
    public const string DefaultAudience = "fcc-middleware-api";

    /// <summary>Expected 'iss' claim. Defaults to "fcc-middleware-cloud".</summary>
    public string Issuer { get; init; } = DefaultIssuer;

    /// <summary>Expected 'aud' claim. Defaults to "fcc-middleware-api".</summary>
    public string Audience { get; init; } = DefaultAudience;
}

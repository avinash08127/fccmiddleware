namespace FccMiddleware.Api.Auth;

/// <summary>
/// Configuration for Azure Entra / MSAL access token validation on portal endpoints.
/// Bound from appsettings.json section "PortalJwt".
/// </summary>
public sealed class PortalJwtOptions
{
    public const string SectionName = "PortalJwt";
    public const string SchemeName = "PortalBearer";

    /// <summary>
    /// Entra authority, for example:
    /// https://login.microsoftonline.com/{tenantId}/v2.0
    /// </summary>
    public string Authority { get; init; } = string.Empty;

    /// <summary>
    /// Expected token audience. If omitted, ClientId is used instead.
    /// </summary>
    public string Audience { get; init; } = string.Empty;

    /// <summary>
    /// Portal/API app registration client ID. Also accepted as an audience,
    /// together with the api://{clientId} form.
    /// </summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Extra accepted audiences for environments that expose the API under
    /// multiple Entra audience identifiers.
    /// </summary>
    public string[] AdditionalAudiences { get; init; } = [];

    /// <summary>
    /// Test-only symmetric signing key used by integration tests in place of
    /// Entra OpenID metadata discovery.
    /// </summary>
    public string SigningKey { get; init; } = string.Empty;
}

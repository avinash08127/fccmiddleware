using Microsoft.AspNetCore.Authentication;

namespace FccMiddleware.Api.Auth;

/// <summary>
/// Options for the Odoo API key authentication scheme.
/// </summary>
public sealed class OdooApiKeyAuthOptions : AuthenticationSchemeOptions
{
    /// <summary>Authentication scheme name registered in AddAuthentication.</summary>
    public const string SchemeName = "OdooApiKey";

    /// <summary>Request header that carries the raw API key.</summary>
    public const string HeaderName = "X-Api-Key";

    /// <summary>Authorization policy name that requires this scheme.</summary>
    public const string PolicyName = "OdooApiKey";
}

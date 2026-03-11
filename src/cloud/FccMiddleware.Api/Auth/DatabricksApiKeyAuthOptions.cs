using Microsoft.AspNetCore.Authentication;

namespace FccMiddleware.Api.Auth;

/// <summary>
/// Options for the Databricks API key authentication scheme.
/// Used by master data sync endpoints (PUT /api/v1/master-data/*).
/// </summary>
public sealed class DatabricksApiKeyAuthOptions : AuthenticationSchemeOptions
{
    /// <summary>Authentication scheme name registered in AddAuthentication.</summary>
    public const string SchemeName = "DatabricksApiKey";

    /// <summary>Request header that carries the raw API key.</summary>
    public const string HeaderName = "X-Api-Key";

    /// <summary>Authorization policy name that requires this scheme.</summary>
    public const string PolicyName = "DatabricksApiKey";

    /// <summary>Role value that must be present on a valid Databricks API key.</summary>
    public const string RequiredRole = "master-data-sync";
}

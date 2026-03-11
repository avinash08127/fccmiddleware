using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Api.Auth;

/// <summary>
/// ASP.NET Core authentication handler for the Databricks API key scheme.
///
/// Flow:
///   1. Extract the raw key from the X-Api-Key request header.
///   2. Compute its SHA-256 hex hash.
///   3. Look up the hash in the databricks_api_keys table.
///   4. Validate IsActive, not revoked, not expired, role = "master-data-sync".
///   5. Issue a ClaimsPrincipal carrying the 'role' claim.
/// </summary>
public sealed class DatabricksApiKeyAuthHandler : AuthenticationHandler<DatabricksApiKeyAuthOptions>
{
    public DatabricksApiKeyAuthHandler(
        IOptionsMonitor<DatabricksApiKeyAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(DatabricksApiKeyAuthOptions.HeaderName, out var headerValues)
            || string.IsNullOrWhiteSpace(headerValues.FirstOrDefault()))
        {
            return AuthenticateResult.NoResult();
        }

        var rawKey = headerValues.ToString();
        var keyHash = ComputeSha256Hex(rawKey);

        var db = Context.RequestServices.GetRequiredService<FccMiddlewareDbContext>();

        var apiKey = await db.DatabricksApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash, Context.RequestAborted);

        if (apiKey is null || !apiKey.IsValid(DateTimeOffset.UtcNow))
            return AuthenticateResult.Fail("Invalid or expired Databricks API key.");

        if (apiKey.Role != DatabricksApiKeyAuthOptions.RequiredRole)
            return AuthenticateResult.Fail($"API key role '{apiKey.Role}' is not authorised for this endpoint.");

        var claims = new[]
        {
            new Claim("role",   apiKey.Role),
            new Claim("key_id", apiKey.Id.ToString()),
            new Claim(ClaimTypes.Name, apiKey.Label)
        };

        var identity  = new ClaimsIdentity(claims, DatabricksApiKeyAuthOptions.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, DatabricksApiKeyAuthOptions.SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    private static string ComputeSha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

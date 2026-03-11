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
/// ASP.NET Core authentication handler for the Odoo API key scheme.
///
/// Flow:
///   1. Extract the raw key from the X-Api-Key request header.
///   2. Compute its SHA-256 hex hash.
///   3. Look up the hash in the odoo_api_keys table.
///   4. If found and <see cref="Domain.Entities.OdooApiKey.IsValid"/> returns true,
///      issue a ClaimsPrincipal carrying the 'lei' (legalEntityId) claim.
///
/// The 'lei' claim is read by the controller to scope the transaction query.
/// </summary>
public sealed class OdooApiKeyAuthHandler : AuthenticationHandler<OdooApiKeyAuthOptions>
{
    public OdooApiKeyAuthHandler(
        IOptionsMonitor<OdooApiKeyAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(OdooApiKeyAuthOptions.HeaderName, out var headerValues)
            || string.IsNullOrWhiteSpace(headerValues.FirstOrDefault()))
        {
            // No key present — let the framework decide (401 if endpoint requires auth).
            return AuthenticateResult.NoResult();
        }

        var rawKey = headerValues.ToString();
        var keyHash = ComputeSha256Hex(rawKey);

        var db = Context.RequestServices.GetRequiredService<FccMiddlewareDbContext>();

        var apiKey = await db.OdooApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash, Context.RequestAborted);

        if (apiKey is null || !apiKey.IsValid(DateTimeOffset.UtcNow))
            return AuthenticateResult.Fail("Invalid or expired Odoo API key.");

        var claims = new[]
        {
            new Claim("lei",    apiKey.LegalEntityId.ToString()),
            new Claim("key_id", apiKey.Id.ToString()),
            new Claim(ClaimTypes.Name, apiKey.Label)
        };

        var identity  = new ClaimsIdentity(claims, OdooApiKeyAuthOptions.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, OdooApiKeyAuthOptions.SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    private static string ComputeSha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Api.Auth;

public sealed class FccHmacAuthHandler : AuthenticationHandler<FccHmacAuthOptions>
{
    public FccHmacAuthHandler(
        IOptionsMonitor<FccHmacAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(FccHmacAuthOptions.ApiKeyHeaderName, out var apiKeyHeader)
            || string.IsNullOrWhiteSpace(apiKeyHeader.FirstOrDefault()))
        {
            return AuthenticateResult.NoResult();
        }

        if (!Request.Headers.TryGetValue(FccHmacAuthOptions.SignatureHeaderName, out var signatureHeader)
            || string.IsNullOrWhiteSpace(signatureHeader.FirstOrDefault()))
        {
            return AuthenticateResult.Fail("Missing HMAC signature.");
        }

        if (!Request.Headers.TryGetValue(FccHmacAuthOptions.TimestampHeaderName, out var timestampHeader)
            || string.IsNullOrWhiteSpace(timestampHeader.FirstOrDefault()))
        {
            return AuthenticateResult.Fail("Missing HMAC timestamp.");
        }

        var client = Options.Clients.FirstOrDefault(candidate =>
            candidate.Active
            && string.Equals(candidate.ApiKeyId, apiKeyHeader.ToString(), StringComparison.Ordinal));

        if (client is null || string.IsNullOrWhiteSpace(client.Secret))
        {
            return AuthenticateResult.Fail("Unknown FCC API key.");
        }

        if (!TryParseTimestamp(timestampHeader.ToString(), out var timestamp))
        {
            return AuthenticateResult.Fail("Invalid HMAC timestamp.");
        }

        var clockSkew = Math.Abs((DateTimeOffset.UtcNow - timestamp).TotalMinutes);
        if (clockSkew > Options.AllowedClockSkewMinutes)
        {
            return AuthenticateResult.Fail("HMAC timestamp is outside the allowed clock skew.");
        }

        // S-3: Reject oversized bodies before buffering to prevent memory pressure.
        // Kestrel's MaxRequestBodySize (5 MB) enforces at transport level; this is
        // defence-in-depth for when Content-Length is known upfront.
        const long maxHmacBodyBytes = 5 * 1024 * 1024;
        if (Request.ContentLength > maxHmacBodyBytes)
        {
            return AuthenticateResult.Fail("Request body exceeds maximum allowed size.");
        }

        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(Context.RequestAborted);
        Request.Body.Position = 0;

        var bodyHash = ComputeSha256Hex(body);
        var canonical = $"{Request.Method}{Request.Path}{timestampHeader}{bodyHash}";
        var expectedSignature = ComputeHmacSha256Hex(client.Secret, canonical);

        if (!FixedTimeEquals(expectedSignature, signatureHeader.ToString()))
        {
            return AuthenticateResult.Fail("Invalid FCC HMAC signature.");
        }

        var claims = new List<Claim>
        {
            new("fcc_api_key_id", client.ApiKeyId),
            new(ClaimTypes.NameIdentifier, client.ApiKeyId)
        };

        if (!string.IsNullOrWhiteSpace(client.SiteCode))
        {
            claims.Add(new Claim("site", client.SiteCode));
        }

        if (client.LegalEntityId.HasValue)
        {
            claims.Add(new Claim("lei", client.LegalEntityId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(claims, FccHmacAuthOptions.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, FccHmacAuthOptions.SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochSeconds))
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            return true;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static string ComputeSha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeHmacSha256Hex(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left.Trim());
        var rightBytes = Encoding.UTF8.GetBytes(right.Trim());
        return leftBytes.Length == rightBytes.Length
               && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

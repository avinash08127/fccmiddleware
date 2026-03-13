using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FccMiddleware.Application.Registration;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace FccMiddleware.Api.Auth;

/// <summary>
/// Generates signed device JWTs for Edge Agent authentication.
/// Uses HMAC-SHA256 with the same signing key used for validation.
/// </summary>
public sealed class DeviceTokenService : IDeviceTokenService
{
    private readonly IConfiguration _config;

    public DeviceTokenService(IConfiguration config)
    {
        _config = config;
    }

    /// <inheritdoc />
    public Guid? ExtractDeviceIdFromExpiredToken(string token)
    {
        var jwtSection = _config.GetSection(DeviceJwtOptions.SectionName);
        var signingKey = jwtSection["SigningKey"] ?? string.Empty;
        var issuer = jwtSection["Issuer"] ?? DeviceJwtOptions.DefaultIssuer;
        var audience = jwtSection["Audience"] ?? DeviceJwtOptions.DefaultAudience;

        if (string.IsNullOrWhiteSpace(signingKey))
            return null;

        var keyBytes = Encoding.UTF8.GetBytes(signingKey);
        var key = new SymmetricSecurityKey(keyBytes);

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = false, // FM-S03: Allow expired tokens
        };

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, validationParameters, out _);
            var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                   ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return sub is not null && Guid.TryParse(sub, out var deviceId) ? deviceId : null;
        }
        catch
        {
            return null;
        }
    }

    public (string Token, DateTimeOffset ExpiresAt) GenerateDeviceToken(
        Guid deviceId, string siteCode, Guid legalEntityId)
    {
        var jwtSection = _config.GetSection(DeviceJwtOptions.SectionName);
        var signingKey = jwtSection["SigningKey"] ?? string.Empty;
        var issuer = jwtSection["Issuer"] ?? DeviceJwtOptions.DefaultIssuer;
        var audience = jwtSection["Audience"] ?? DeviceJwtOptions.DefaultAudience;

        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException(
                "DeviceJwt:SigningKey is not configured. Cannot generate device tokens without a signing key. " +
                "Ensure the signing key is provisioned via environment variables or a secrets manager.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(signingKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                $"DeviceJwt:SigningKey is too short ({keyBytes.Length} bytes). " +
                "HMAC-SHA256 requires a key of at least 256 bits (32 bytes).");
        }

        var key = new SymmetricSecurityKey(keyBytes);
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddHours(24);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, deviceId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("site", siteCode),
            new Claim("lei", legalEntityId.ToString()),
            new Claim("roles", "edge-agent")
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Api.Auth;

/// <summary>
/// Enforces X-Api-Key authentication on every request.
/// Architecture rule #14: All API requests require API key — no localhost bypass.
/// Odoo POS is always on a separate HHT device and always connects over LAN.
///
/// Uses constant-time comparison to prevent timing side-channel attacks.
/// </summary>
internal sealed class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<LocalApiOptions> _options;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(
        RequestDelegate next,
        IOptionsMonitor<LocalApiOptions> options,
        ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var configuredKey = _options.CurrentValue.ApiKey;

        // S-DSK-027: Fail closed — refuse requests when no API key is configured.
        // A missing key means the credential store was not populated during provisioning
        // or failed to load at startup. Allowing requests without auth would expose the
        // pre-auth and transaction APIs to any device on the LAN.
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            _logger.LogWarning("LocalApi.ApiKey is not configured — rejecting request. " +
                "Provision the agent or set LocalApi:ApiKey before production use.");

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                errorCode = "AUTH_NOT_CONFIGURED",
                message = "API key authentication is not yet configured. Complete agent provisioning.",
                traceId = context.TraceIdentifier,
                timestamp = DateTimeOffset.UtcNow
            });
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey)
            || !ConstantTimeEquals(providedKey.ToString(), configuredKey))
        {
            _logger.LogWarning("Rejected request {Method} {Path} — missing or invalid {Header}",
                context.Request.Method, context.Request.Path, ApiKeyHeader);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                errorCode = "UNAUTHORIZED",
                message = "Missing or invalid X-Api-Key header",
                traceId = context.TraceIdentifier,
                timestamp = DateTimeOffset.UtcNow
            });
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Compares two strings in constant time to prevent timing side-channel attacks.
    /// Uses <see cref="CryptographicOperations.FixedTimeEquals"/> on UTF-8 byte spans.
    /// </summary>
    internal static bool ConstantTimeEquals(string a, string b)
    {
        // Cap input length to prevent stack overflow from oversized headers.
        // API keys longer than 1024 chars are rejected outright.
        const int MaxKeyLength = 1024;
        if (a.Length > MaxKeyLength || b.Length > MaxKeyLength)
            return false;

        if (a.Length != b.Length)
        {
            // Even though the length mismatch leaks length information, we still
            // need FixedTimeEquals on padded buffers to avoid early-exit on content.
            // For API keys of equal expected length this branch is rarely hit.
            var maxLen = Math.Max(a.Length, b.Length);
            Span<byte> padA = stackalloc byte[maxLen];
            Span<byte> padB = stackalloc byte[maxLen];
            padA.Clear();
            padB.Clear();
            Encoding.UTF8.GetBytes(a, padA);
            Encoding.UTF8.GetBytes(b, padB);
            return CryptographicOperations.FixedTimeEquals(padA, padB);
        }

        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}

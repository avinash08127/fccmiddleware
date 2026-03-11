using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Api.Auth;

/// <summary>
/// Enforces X-Api-Key authentication on every request.
/// Architecture rule #14: All API requests require API key — no localhost bypass.
/// Odoo POS is always on a separate HHT device and always connects over LAN.
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

        // No key configured → auth disabled (dev / unprovisioned). Warn and allow.
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            _logger.LogWarning("LocalApi.ApiKey is not configured — authentication is DISABLED. Set LocalApi:ApiKey before production use.");
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey)
            || providedKey.ToString() != configuredKey)
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
}

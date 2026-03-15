using FccDesktopAgent.Api.Auth;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Api.Endpoints;

/// <summary>
/// ASP.NET Core WebSocket middleware bridge for the Odoo backward-compat protocol.
///
/// Usage in Program.cs:
/// <code>
/// app.UseWebSockets();
/// app.MapOdooWebSocket();
/// </code>
///
/// The middleware upgrades HTTP requests at <c>/ws</c> and <c>/</c> (root path) to WebSocket
/// connections, matching the legacy DOMSRealImplementation Fleck server behavior.
/// </summary>
public static class OdooWsBridge
{
    /// <summary>
    /// Maps the WebSocket upgrade handler at <c>/ws</c> and <c>/</c> (root).
    /// Must be called after <c>app.UseWebSockets()</c>.
    /// </summary>
    public static WebApplication MapOdooWebSocket(this WebApplication app)
    {
        var wsServer = app.Services.GetRequiredService<OdooWebSocketServer>();
        var options = app.Services.GetRequiredService<IOptions<WebSocketServerOptions>>().Value;
        var apiOptions = app.Services.GetRequiredService<IOptionsMonitor<LocalApiOptions>>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("OdooWsBridge");

        wsServer.Options = options;

        // Wire adapter and pump status service (available as singletons)
        wsServer.PumpStatusService = app.Services.GetService<IPumpStatusService>();

        if (!options.Enabled)
        {
            logger.LogDebug("Odoo WebSocket server disabled in configuration");
            return app;
        }

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Expected WebSocket upgrade");
                return;
            }

            // F-DSK-012: Validate API key on the HTTP upgrade request before accepting
            // the WebSocket connection, matching the REST API's ApiKeyMiddleware gate.
            if (!ValidateApiKey(context, apiOptions, logger))
                return;

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            logger.LogInformation("WebSocket upgrade accepted from {RemoteIp}",
                context.Connection.RemoteIpAddress);

            await wsServer.HandleConnectionAsync(ws, context.RequestAborted);
        });

        // Accept WebSocket at root path for legacy compatibility — exact path match only.
        // Non-WebSocket requests fall through to the normal pipeline (health, API, etc.).
        app.MapWhen(
            context => context.Request.Path == "/" && context.WebSockets.IsWebSocketRequest,
            rootApp => rootApp.Run(async context =>
            {
                // F-DSK-012: Validate API key on the HTTP upgrade request.
                if (!ValidateApiKey(context, apiOptions, logger))
                    return;

                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                logger.LogInformation("WebSocket upgrade accepted at root from {RemoteIp}",
                    context.Connection.RemoteIpAddress);

                await wsServer.HandleConnectionAsync(ws, context.RequestAborted);
            }));

        logger.LogInformation("Odoo WebSocket server mapped at /ws and / (port configured in Kestrel)");

        return app;
    }

    /// <summary>
    /// F-DSK-012: Validates the API key on the WebSocket HTTP upgrade request.
    /// Accepts the key via the <c>X-Api-Key</c> header or <c>apiKey</c> query parameter
    /// (WebSocket clients often cannot set custom headers).
    /// Returns <c>true</c> if authenticated, <c>false</c> if the request was rejected (401 already written).
    /// </summary>
    private static bool ValidateApiKey(
        HttpContext context,
        IOptionsMonitor<LocalApiOptions> apiOptions,
        ILogger logger)
    {
        var configuredKey = apiOptions.CurrentValue.ApiKey;

        // S-DSK-027: Fail closed — reject WebSocket upgrades when no API key is configured.
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            logger.LogWarning("WebSocket upgrade rejected — no API key configured. Complete agent provisioning.");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return false;
        }

        // Check header first, then query parameter
        var providedKey = context.Request.Headers.TryGetValue("X-Api-Key", out var headerKey)
            ? headerKey.ToString()
            : context.Request.Query.TryGetValue("apiKey", out var queryKey)
                ? queryKey.ToString()
                : null;

        if (providedKey is null || !ApiKeyMiddleware.ConstantTimeEquals(providedKey, configuredKey))
        {
            logger.LogWarning(
                "WebSocket upgrade rejected from {RemoteIp} — missing or invalid API key",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }

        return true;
    }
}

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

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            logger.LogInformation("WebSocket upgrade accepted from {RemoteIp}",
                context.Connection.RemoteIpAddress);

            await wsServer.HandleConnectionAsync(ws, context.RequestAborted);
        });

        // Also accept at root path for legacy compatibility
        app.Map("/", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                // Non-WebSocket requests to "/" fall through to the normal API
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsync("FCC Desktop Edge Agent");
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            logger.LogInformation("WebSocket upgrade accepted at root from {RemoteIp}",
                context.Connection.RemoteIpAddress);

            await wsServer.HandleConnectionAsync(ws, context.RequestAborted);
        });

        logger.LogInformation("Odoo WebSocket server mapped at /ws and / (port configured in Kestrel)");

        return app;
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FccDesktopAgent.Api.Endpoints;

/// <summary>
/// Agent status endpoint — operational health summary for Odoo POS and supervisor UIs.
/// p95 target: &lt;= 50 ms.
/// </summary>
internal static class StatusEndpoints
{
    private static readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    internal static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/status")
            .WithTags("Status");

        // GET /api/v1/status — agent operational status
        // Returns 200 with placeholder values. DEA-2.x: inject IConnectivityMonitor + buffer stats.
        group.MapGet("/", () =>
            Results.Ok(new
            {
                deviceId = "00000000-0000-0000-0000-000000000000",
                siteCode = "UNPROVISIONED",
                connectivityState = "FULLY_OFFLINE",
                fccReachable = false,
                fccHeartbeatAgeSeconds = (int?)null,
                bufferDepth = 0,
                syncLagSeconds = (int?)null,
                lastSuccessfulSyncUtc = (DateTimeOffset?)null,
                configVersion = (int?)null,
                agentVersion = "0.1.0",
                uptimeSeconds = (int)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
                reportedAtUtc = DateTimeOffset.UtcNow
            }))
            .WithName("getAgentStatus");

        return app;
    }
}

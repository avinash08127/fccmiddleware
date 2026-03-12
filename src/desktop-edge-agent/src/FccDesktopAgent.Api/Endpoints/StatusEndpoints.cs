using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Api.Endpoints;

/// <summary>
/// Agent status endpoint — operational health summary for Odoo POS and supervisor UIs.
/// p95 target: &lt;= 50 ms.
/// </summary>
internal static class StatusEndpoints
{
    private static readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private static readonly string _agentVersion =
        typeof(StatusEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    internal static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/status")
            .WithTags("Status");

        // GET /api/v1/status — agent operational status
        group.MapGet("/", async (
            IConnectivityMonitor connectivity,
            IOptions<AgentConfiguration> config,
            IConfigManager configManager,
            TransactionBufferManager buffer,
            CancellationToken ct) =>
        {
            var cfg = config.Value;
            var snapshot = connectivity.Current;
            var stats = await buffer.GetBufferStatsAsync(ct);

            var syncLagSeconds = stats.OldestPendingAtUtc.HasValue
                ? (int?)(DateTimeOffset.UtcNow - stats.OldestPendingAtUtc.Value).TotalSeconds
                : null;

            var fccHeartbeatAgeSeconds = connectivity.LastFccSuccessAtUtc.HasValue
                ? (int?)(DateTimeOffset.UtcNow - connectivity.LastFccSuccessAtUtc.Value).TotalSeconds
                : null;

            return Results.Ok(new
            {
                deviceId = cfg.DeviceId,
                siteCode = cfg.SiteId,
                connectivityState = snapshot.State,
                fccReachable = snapshot.IsFccUp,
                fccHeartbeatAgeSeconds,
                bufferDepth = stats.Pending,
                syncLagSeconds,
                lastSuccessfulSyncUtc = (DateTimeOffset?)null,
                configVersion = configManager.CurrentConfigVersion,
                agentVersion = _agentVersion,
                uptimeSeconds = (int)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
                reportedAtUtc = DateTimeOffset.UtcNow
            });
        })
        .WithName("getAgentStatus");

        return app;
    }
}

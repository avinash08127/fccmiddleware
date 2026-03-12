using FccDesktopAgent.Core.Adapter.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FccDesktopAgent.Api.Endpoints;

/// <summary>
/// Pump status endpoint — live FCC pump state with single-flight protection and stale fallback.
/// Architecture rule #13: SemaphoreSlim single-flight + last-known stale fallback when FCC is slow.
/// Live target: &lt;= 1 s; stale fallback: &lt;= 50 ms.
/// </summary>
internal static class PumpStatusEndpoints
{
    internal static IEndpointRouteBuilder MapPumpStatusEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pump-status")
            .WithTags("PumpStatus");

        // GET /api/v1/pump-status — live pump statuses with optional pumpNumber filter
        group.MapGet("/", async (
            int? pumpNumber,
            IPumpStatusService pumpStatusService,
            CancellationToken ct) =>
        {
            var result = await pumpStatusService.GetPumpStatusAsync(pumpNumber, ct);

            if (result.Source == "unavailable")
            {
                return Results.Json(
                    new
                    {
                        errorCode = "FCC_UNAVAILABLE",
                        message = "FCC is unreachable and no cached pump status is available",
                        timestamp = DateTimeOffset.UtcNow
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new
            {
                pumps = result.Pumps,
                source = result.Source,
                cachedAtUtc = result.CachedAtUtc,
                observedAtUtc = result.ObservedAtUtc,
            });
        })
        .WithName("getPumpStatus");

        return app;
    }
}

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
        // DEA-2.x: inject IFccAdapter with single-flight guard and stale cache
        group.MapGet("/", (int? pumpNumber) =>
            NotImplemented("GET /api/v1/pump-status"))
            .WithName("getPumpStatus");

        return app;
    }

    private static IResult NotImplemented(string endpoint) =>
        Results.Json(
            new
            {
                errorCode = "NOT_IMPLEMENTED",
                message = $"{endpoint} is not yet implemented",
                traceId = (string?)null,
                timestamp = DateTimeOffset.UtcNow
            },
            statusCode: StatusCodes.Status501NotImplemented);
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FccDesktopAgent.Api.Endpoints;

/// <summary>
/// Pre-authorisation endpoints — relay pre-auth commands from Odoo POS to the FCC over LAN.
/// Architecture rule #11: Cloud forwarding is always async, never on the request path.
/// p95 end-to-end target on healthy LAN: &lt;= 1.5 s; p99 &lt;= 3 s.
/// </summary>
internal static class PreAuthEndpoints
{
    internal static IEndpointRouteBuilder MapPreAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/preauth")
            .WithTags("PreAuth");

        // POST /api/v1/preauth — submit pre-auth request
        // Blocks until FCC responds or timeout expires (preAuthTimeoutSeconds from SiteConfig).
        // customerTaxId is PII — NEVER log (architecture rule #9).
        // DEA-2.x: inject IPreAuthHandler
        group.MapPost("/", () =>
            NotImplemented("POST /api/v1/preauth"))
            .WithName("submitPreAuth");

        // DELETE /api/v1/preauth/{id} — cancel pre-auth
        // Idempotent: cancelling CANCELLED → 200; terminal state (COMPLETED, EXPIRED, FAILED) → 409
        // DEA-2.x: inject IPreAuthHandler
        group.MapDelete("/{id:guid}", (Guid id) =>
            NotImplemented("DELETE /api/v1/preauth/{id}"))
            .WithName("cancelPreAuth");

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

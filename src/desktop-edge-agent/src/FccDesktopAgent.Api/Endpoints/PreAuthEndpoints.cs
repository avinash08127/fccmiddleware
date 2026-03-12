using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.PreAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FccDesktopAgent.Core.Config;

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
        group.MapPost("/", async (
            [FromBody] SubmitPreAuthRequest request,
            IPreAuthHandler handler,
            IOptions<AgentConfiguration> config,
            ILogger<IPreAuthHandler> logger,
            CancellationToken ct) =>
        {
            // ── Input validation ──
            if (string.IsNullOrWhiteSpace(request.OdooOrderId))
                return ValidationError("odooOrderId is required");
            if (string.IsNullOrWhiteSpace(request.SiteCode))
                return ValidationError("siteCode is required");
            if (request.OdooPumpNumber <= 0)
                return ValidationError("odooPumpNumber must be > 0");
            if (request.OdooNozzleNumber <= 0)
                return ValidationError("odooNozzleNumber must be > 0");
            if (request.RequestedAmountMinorUnits <= 0)
                return ValidationError("requestedAmountMinorUnits must be > 0");
            if (request.UnitPriceMinorPerLitre <= 0)
                return ValidationError("unitPriceMinorPerLitre must be > 0");
            if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != 3)
                return ValidationError("currency must be a 3-letter ISO 4217 code");

            var domainRequest = new OdooPreAuthRequest(
                OdooOrderId: request.OdooOrderId.Trim(),
                SiteCode: request.SiteCode.Trim(),
                OdooPumpNumber: request.OdooPumpNumber,
                OdooNozzleNumber: request.OdooNozzleNumber,
                RequestedAmountMinorUnits: request.RequestedAmountMinorUnits,
                UnitPriceMinorPerLitre: request.UnitPriceMinorPerLitre,
                Currency: request.Currency.Trim().ToUpperInvariant(),
                VehicleNumber: request.VehicleNumber?.Trim(),
                CustomerName: request.CustomerName?.Trim(),
                CustomerTaxId: request.CustomerTaxId?.Trim(),
                CustomerBusinessName: request.CustomerBusinessName?.Trim(),
                AttendantId: request.AttendantId?.Trim());

            var result = await handler.HandleAsync(domainRequest, ct);

            if (result.IsSuccess)
            {
                return Results.Ok(new PreAuthResponse(
                    PreAuthId: result.RecordId!,
                    Status: result.Status!.Value,
                    FccAuthorizationCode: result.FccAuthorizationCode,
                    FccCorrelationId: result.FccCorrelationId,
                    ExpiresAtUtc: result.ExpiresAt,
                    Timestamp: DateTimeOffset.UtcNow));
            }

            return MapHandlerError(result);
        })
        .WithName("submitPreAuth");

        // DELETE /api/v1/preauth/{id} — cancel pre-auth by Odoo order ID
        // Idempotent: cancelling CANCELLED → 200; terminal state (COMPLETED, EXPIRED, FAILED) → 200
        // Dispensing → 409
        group.MapDelete("/{odooOrderId}", async (
            string odooOrderId,
            [FromQuery] string siteCode,
            IPreAuthHandler handler,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(odooOrderId))
                return ValidationError("odooOrderId is required");
            if (string.IsNullOrWhiteSpace(siteCode))
                return ValidationError("siteCode query parameter is required");

            var result = await handler.CancelAsync(odooOrderId.Trim(), siteCode.Trim(), ct);

            if (result.IsSuccess)
            {
                return Results.Ok(new PreAuthResponse(
                    PreAuthId: result.RecordId!,
                    Status: result.Status!.Value,
                    FccAuthorizationCode: result.FccAuthorizationCode,
                    FccCorrelationId: result.FccCorrelationId,
                    ExpiresAtUtc: result.ExpiresAt,
                    Timestamp: DateTimeOffset.UtcNow));
            }

            return MapHandlerError(result);
        })
        .WithName("cancelPreAuth");

        return app;
    }

    private static IResult ValidationError(string message) =>
        Results.Json(
            new
            {
                errorCode = "VALIDATION_ERROR",
                message,
                timestamp = DateTimeOffset.UtcNow
            },
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult MapHandlerError(PreAuthHandlerResult result)
    {
        var (statusCode, errorCode) = result.Error switch
        {
            PreAuthHandlerError.NozzleMappingNotFound => (StatusCodes.Status404NotFound, "NOZZLE_MAPPING_NOT_FOUND"),
            PreAuthHandlerError.NozzleInactive => (StatusCodes.Status422UnprocessableEntity, "NOZZLE_INACTIVE"),
            PreAuthHandlerError.FccUnreachable => (StatusCodes.Status503ServiceUnavailable, "FCC_UNREACHABLE"),
            PreAuthHandlerError.AdapterNotConfigured => (StatusCodes.Status503ServiceUnavailable, "ADAPTER_NOT_CONFIGURED"),
            PreAuthHandlerError.UnsupportedVendor => (StatusCodes.Status422UnprocessableEntity, "UNSUPPORTED_VENDOR"),
            PreAuthHandlerError.FccDeclined => (StatusCodes.Status422UnprocessableEntity, "FCC_DECLINED"),
            PreAuthHandlerError.FccTimeout => (StatusCodes.Status504GatewayTimeout, "FCC_TIMEOUT"),
            PreAuthHandlerError.RecordNotFound => (StatusCodes.Status404NotFound, "RECORD_NOT_FOUND"),
            PreAuthHandlerError.CannotCancelDispensing => (StatusCodes.Status409Conflict, "CANNOT_CANCEL_DISPENSING"),
            _ => (StatusCodes.Status500InternalServerError, "UNKNOWN_ERROR"),
        };

        return Results.Json(
            new
            {
                errorCode,
                message = result.ErrorDetail ?? "Pre-auth operation failed",
                timestamp = DateTimeOffset.UtcNow
            },
            statusCode: statusCode);
    }
}

// ── Request/Response DTOs ─────────────────────────────────────────────────────

/// <summary>
/// Request body for POST /api/v1/preauth.
/// </summary>
internal sealed record SubmitPreAuthRequest(
    string OdooOrderId,
    string SiteCode,
    int OdooPumpNumber,
    int OdooNozzleNumber,
    long RequestedAmountMinorUnits,
    long UnitPriceMinorPerLitre,
    string Currency,
    string? VehicleNumber = null,
    string? CustomerName = null,
    string? CustomerTaxId = null,
    string? CustomerBusinessName = null,
    string? AttendantId = null);

/// <summary>
/// Response for pre-auth submit and cancel operations.
/// </summary>
internal sealed record PreAuthResponse(
    string PreAuthId,
    PreAuthStatus Status,
    string? FccAuthorizationCode,
    string? FccCorrelationId,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset Timestamp);

using System.Diagnostics;
using System.Security.Claims;
using FccMiddleware.Api.Infrastructure;
using FccMiddleware.Application.PreAuth;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.PreAuth;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FccMiddleware.Api.Controllers;

/// <summary>
/// Handles pre-authorization lifecycle forwarding from Edge Agents.
/// POST /api/v1/preauth — Edge Agent forwards pre-auth result for cloud tracking.
/// </summary>
[ApiController]
[Route("api/v1/preauth")]
public sealed class PreAuthController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IAuthoritativeWriteFenceService _writeFence;

    public PreAuthController(IMediator mediator, IAuthoritativeWriteFenceService writeFence)
    {
        _mediator = mediator;
        _writeFence = writeFence;
    }

    /// <summary>
    /// Forwards a pre-auth record from the Edge Agent to cloud for lifecycle tracking.
    /// </summary>
    /// <remarks>
    /// Dedup key: (odooOrderId, siteCode). Re-posting with the same key and an updated
    /// status updates the existing record. Terminal-status records allow re-request.
    /// Returns 201 when a new record is created; 200 when an existing record is updated.
    /// Returns 409 for invalid state transitions.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(typeof(PreAuthForwardResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PreAuthForwardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ForwardPreAuth(
        [FromBody] PreAuthForwardRequest request,
        CancellationToken cancellationToken)
    {
        // ── Validate status enum ──────────────────────────────────────────────
        if (!Enum.TryParse<PreAuthStatus>(request.Status, ignoreCase: true, out var status))
        {
            return BadRequest(BuildError(
                "VALIDATION.INVALID_STATUS",
                $"Unknown pre-auth status '{request.Status}'. Valid values: {string.Join(", ", Enum.GetNames<PreAuthStatus>())}"));
        }

        // ── Extract JWT claims ────────────────────────────────────────────────
        var siteCode = User.FindFirstValue("site") ?? string.Empty;
        var deviceId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;
        var leiStr = User.FindFirstValue("lei") ?? string.Empty;

        if (!Guid.TryParse(leiStr, out var legalEntityId))
        {
            return BadRequest(BuildError(
                "VALIDATION.INVALID_LEI",
                "JWT 'lei' claim is not a valid UUID."));
        }

        // ── Validate site claim matches request ───────────────────────────────
        if (!string.Equals(siteCode, request.SiteCode, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(BuildError(
                "VALIDATION.SITE_MISMATCH",
                $"JWT site claim '{siteCode}' does not match request siteCode '{request.SiteCode}'."));
        }

        // ── Basic field validation ────────────────────────────────────────────
        if (request.RequestedAmount <= 0)
        {
            return BadRequest(BuildError(
                "VALIDATION.INVALID_AMOUNT",
                "requestedAmount must be greater than 0."));
        }

        if (request.UnitPrice <= 0)
        {
            return BadRequest(BuildError(
                "VALIDATION.INVALID_UNIT_PRICE",
                "unitPrice must be greater than 0."));
        }

        if (request.PumpNumber <= 0)
        {
            return BadRequest(BuildError(
                "VALIDATION.INVALID_PUMP_NUMBER",
                "pumpNumber must be greater than 0."));
        }

        if (request.NozzleNumber <= 0)
        {
            return BadRequest(BuildError(
                "VALIDATION.INVALID_NOZZLE_NUMBER",
                "nozzleNumber must be greater than 0."));
        }

        var fenceResult = await _writeFence.ValidateAsync(deviceId, siteCode, request.LeaderEpoch, cancellationToken);
        if (!fenceResult.IsAllowed)
        {
            return fenceResult.StatusCode switch
            {
                StatusCodes.Status400BadRequest => BadRequest(BuildError(
                    fenceResult.ErrorCode!,
                    fenceResult.Message!,
                    fenceResult.Details)),
                StatusCodes.Status401Unauthorized => Unauthorized(BuildError(
                    fenceResult.ErrorCode!,
                    fenceResult.Message!,
                    fenceResult.Details)),
                StatusCodes.Status403Forbidden => StatusCode(StatusCodes.Status403Forbidden, BuildError(
                    fenceResult.ErrorCode!,
                    fenceResult.Message!,
                    fenceResult.Details)),
                _ => Conflict(BuildError(
                    fenceResult.ErrorCode!,
                    fenceResult.Message!,
                    fenceResult.Details))
            };
        }

        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);

        var command = new ForwardPreAuthCommand
        {
            LegalEntityId = legalEntityId,
            SiteCode = request.SiteCode,
            OdooOrderId = request.OdooOrderId,
            PumpNumber = request.PumpNumber,
            NozzleNumber = request.NozzleNumber,
            ProductCode = request.ProductCode,
            RequestedAmountMinorUnits = request.RequestedAmount,
            UnitPriceMinorPerLitre = request.UnitPrice,
            CurrencyCode = request.Currency,
            Status = status,
            RequestedAt = request.RequestedAt,
            ExpiresAt = request.ExpiresAt,
            FccCorrelationId = request.FccCorrelationId,
            FccAuthorizationCode = request.FccAuthorizationCode,
            VehicleNumber = request.VehicleNumber,
            CustomerName = request.CustomerName,
            CustomerTaxId = request.CustomerTaxId,
            CustomerBusinessName = request.CustomerBusinessName,
            AttendantId = request.AttendantId,
            LeaderEpoch = request.LeaderEpoch,
            CorrelationId = correlationId
        };

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "CONFLICT.INVALID_TRANSITION" => Conflict(BuildError(
                    result.Error.Code, result.Error.Message)),
                "CONFLICT.RACE_CONDITION" => Conflict(BuildError(
                    result.Error.Code, result.Error.Message, retryable: true)),
                _ => BadRequest(BuildError(result.Error.Code, result.Error.Message))
            };
        }

        var value = result.Value!;
        var response = new PreAuthForwardResponse
        {
            Id = value.PreAuthId,
            Status = value.Status.ToString(),
            SiteCode = value.SiteCode,
            OdooOrderId = value.OdooOrderId,
            CreatedAt = value.CreatedAt,
            UpdatedAt = value.UpdatedAt
        };

        return value.Created
            ? StatusCode(StatusCodes.Status201Created, response)
            : Ok(response);
    }

    /// <summary>
    /// Updates the lifecycle status of an existing pre-auth record.
    /// </summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(typeof(PreAuthForwardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdatePreAuthStatus(
        Guid id,
        [FromBody] UpdatePreAuthStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<PreAuthStatus>(request.Status, ignoreCase: true, out var status))
        {
            return BadRequest(BuildError(
                "VALIDATION.INVALID_STATUS",
                $"Unknown pre-auth status '{request.Status}'. Valid values: {string.Join(", ", Enum.GetNames<PreAuthStatus>())}"));
        }

        var siteCode = User.FindFirstValue("site") ?? string.Empty;
        var leiStr = User.FindFirstValue("lei") ?? string.Empty;

        if (!Guid.TryParse(leiStr, out var legalEntityId))
        {
            return BadRequest(BuildError(
                "VALIDATION.INVALID_LEI",
                "JWT 'lei' claim is not a valid UUID."));
        }

        var command = new UpdatePreAuthStatusCommand
        {
            PreAuthId = id,
            LegalEntityId = legalEntityId,
            ExpectedSiteCode = siteCode,
            Status = status,
            FccCorrelationId = request.FccCorrelationId,
            FccAuthorizationCode = request.FccAuthorizationCode,
            FailureReason = request.FailureReason,
            ActualAmountMinorUnits = request.ActualAmount,
            ActualVolumeMillilitres = request.ActualVolume,
            MatchedFccTransactionId = request.MatchedFccTransactionId,
            MatchedTransactionId = request.MatchedTransactionId,
            CorrelationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext)
        };

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "NOT_FOUND.PREAUTH" => NotFound(BuildError(result.Error.Code, result.Error.Message)),
                "CONFLICT.INVALID_TRANSITION" => Conflict(BuildError(result.Error.Code, result.Error.Message)),
                _ => BadRequest(BuildError(result.Error.Code, result.Error.Message))
            };
        }

        var value = result.Value!;
        return Ok(new PreAuthForwardResponse
        {
            Id = value.PreAuthId,
            Status = value.Status.ToString(),
            SiteCode = value.SiteCode,
            OdooOrderId = value.OdooOrderId,
            CreatedAt = value.CreatedAt,
            UpdatedAt = value.UpdatedAt
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ErrorResponse BuildError(
        string errorCode,
        string message,
        object? details = null,
        bool retryable = false) =>
        new()
        {
            ErrorCode = errorCode,
            Message = message,
            Details = details,
            TraceId = Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier,
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Retryable = retryable
        };

}

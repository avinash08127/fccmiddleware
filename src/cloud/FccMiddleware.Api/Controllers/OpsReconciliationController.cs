using System.Diagnostics;
using System.Security.Claims;
using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Reconciliation;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/ops/reconciliation")]
[Authorize(Policy = "PortalUser")]
public sealed class OpsReconciliationController : ControllerBase
{
    private readonly IMediator _mediator;

    public OpsReconciliationController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("exceptions")]
    [ProducesResponseType(typeof(GetReconciliationExceptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetExceptions(
        [FromQuery] Guid? legalEntityId,
        [FromQuery] string? siteCode,
        [FromQuery] string? status,
        [FromQuery] DateTimeOffset? since,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100)
        {
            return BadRequest(BuildError(
                "VALIDATION.INVALID_PAGE_SIZE",
                "pageSize must be between 1 and 100."));
        }

        ReconciliationStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ReconciliationStatus>(status, true, out var value))
            {
                return BadRequest(BuildError(
                    "VALIDATION.INVALID_STATUS",
                    $"Unknown reconciliation status '{status}'."));
            }

            parsedStatus = value;
        }

        var access = ResolvePortalAccess();
        if (!access.IsValid)
        {
            return Forbid();
        }

        if (legalEntityId.HasValue
            && !access.AllowAllLegalEntities
            && !access.ScopedLegalEntityIds.Contains(legalEntityId.Value))
        {
            return Forbid();
        }

        var result = await _mediator.Send(new GetReconciliationExceptionsQuery
        {
            LegalEntityId = legalEntityId,
            ScopedLegalEntityIds = access.ScopedLegalEntityIds,
            AllowAllLegalEntities = access.AllowAllLegalEntities,
            SiteCode = siteCode,
            Status = parsedStatus,
            Since = since,
            Cursor = cursor,
            PageSize = pageSize
        }, cancellationToken);

        return Ok(new GetReconciliationExceptionsResponse
        {
            Data = result.Records.Select(r => new ReconciliationExceptionDto
            {
                ReconciliationId = r.ReconciliationId,
                Status = r.Status.ToString(),
                SiteCode = r.SiteCode,
                LegalEntityId = r.LegalEntityId,
                PumpNumber = r.PumpNumber,
                NozzleNumber = r.NozzleNumber,
                AuthorizedAmountMinorUnits = r.AuthorizedAmountMinorUnits,
                ActualAmountMinorUnits = r.ActualAmountMinorUnits,
                VarianceMinorUnits = r.VarianceMinorUnits,
                VariancePercent = r.VariancePercent,
                MatchMethod = r.MatchMethod,
                AmbiguityFlag = r.AmbiguityFlag,
                CreatedAt = r.CreatedAt,
                LastMatchAttemptAt = r.LastMatchAttemptAt
            }).ToList(),
            Meta = new ReconciliationExceptionPageMeta
            {
                PageSize = result.Records.Count,
                HasMore = result.HasMore,
                NextCursor = result.NextCursor
            }
        });
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "PortalReconciliationReview")]
    [ProducesResponseType(typeof(ReviewReconciliationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public Task<IActionResult> Approve(
        Guid id,
        [FromBody] ReviewReconciliationRequest request,
        CancellationToken cancellationToken) =>
        Review(id, request, ReconciliationStatus.APPROVED, cancellationToken);

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = "PortalReconciliationReview")]
    [ProducesResponseType(typeof(ReviewReconciliationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public Task<IActionResult> Reject(
        Guid id,
        [FromBody] ReviewReconciliationRequest request,
        CancellationToken cancellationToken) =>
        Review(id, request, ReconciliationStatus.REJECTED, cancellationToken);

    private async Task<IActionResult> Review(
        Guid id,
        ReviewReconciliationRequest request,
        ReconciliationStatus targetStatus,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(BuildError(
                "VALIDATION.REASON_REQUIRED",
                "reason is required."));
        }

        var access = ResolvePortalAccess();
        if (!access.IsValid)
        {
            return Forbid();
        }

        var reviewedBy = User.FindFirstValue("oid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? User.FindFirstValue("preferred_username")
            ?? User.Identity?.Name;

        if (string.IsNullOrWhiteSpace(reviewedBy))
        {
            return Forbid();
        }

        var result = await _mediator.Send(new ReviewReconciliationCommand
        {
            ReconciliationId = id,
            TargetStatus = targetStatus,
            Reason = request.Reason,
            ReviewedByUserId = reviewedBy,
            ScopedLegalEntityIds = access.ScopedLegalEntityIds,
            AllowAllLegalEntities = access.AllowAllLegalEntities
        }, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "VALIDATION.REASON_REQUIRED" or "VALIDATION.INVALID_STATUS" =>
                    BadRequest(BuildError(result.Error.Code, result.Error.Message)),
                "NOT_FOUND.RECONCILIATION" =>
                    NotFound(BuildError(result.Error.Code, result.Error.Message)),
                "FORBIDDEN.LEGAL_ENTITY_SCOPE" =>
                    Forbid(),
                "CONFLICT.INVALID_TRANSITION" =>
                    Conflict(BuildError(result.Error.Code, result.Error.Message)),
                _ => BadRequest(BuildError(result.Error.Code, result.Error.Message))
            };
        }

        var value = result.Value!;
        return Ok(new ReviewReconciliationResponse
        {
            ReconciliationId = value.ReconciliationId,
            Status = value.Status.ToString(),
            LegalEntityId = value.LegalEntityId,
            SiteCode = value.SiteCode,
            ReviewedByUserId = value.ReviewedByUserId,
            ReviewedAtUtc = value.ReviewedAtUtc,
            ReviewReason = value.ReviewReason
        });
    }

    private PortalAccess ResolvePortalAccess()
    {
        var roles = User.FindAll("roles")
            .SelectMany(claim => claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allowAll = roles.Contains("SystemAdmin") || roles.Contains("SystemAdministrator");
        var legalEntityClaims = User.FindAll("legal_entities")
            .Select(claim => claim.Value)
            .ToList();

        if (allowAll && (legalEntityClaims.Count == 0 || legalEntityClaims.Any(v => v.Trim() == "*")))
        {
            return new PortalAccess(true, Array.Empty<Guid>(), true);
        }

        var ids = legalEntityClaims
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => value != "*")
            .Select(value => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty)
            .Where(value => value != Guid.Empty)
            .Distinct()
            .ToArray();

        return new PortalAccess(ids.Length > 0 || allowAll, ids, allowAll);
    }

    private ErrorResponse BuildError(string errorCode, string message) =>
        new()
        {
            ErrorCode = errorCode,
            Message = message,
            Details = null,
            TraceId = Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier,
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Retryable = false
        };

    private sealed record PortalAccess(
        bool IsValid,
        IReadOnlyCollection<Guid> ScopedLegalEntityIds,
        bool AllowAllLegalEntities);
}

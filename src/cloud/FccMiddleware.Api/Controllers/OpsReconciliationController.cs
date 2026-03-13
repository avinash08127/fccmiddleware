using FccMiddleware.Api.Portal;
using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Reconciliation;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/ops/reconciliation")]
[Authorize(Policy = "PortalUser")]
public sealed class OpsReconciliationController : PortalControllerBase
{
    private readonly FccMiddlewareDbContext _db;
    private readonly IMediator _mediator;
    private readonly PortalAccessResolver _accessResolver;

    public OpsReconciliationController(
        FccMiddlewareDbContext db,
        IMediator mediator,
        PortalAccessResolver accessResolver)
    {
        _db = db;
        _mediator = mediator;
        _accessResolver = accessResolver;
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
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] DateTimeOffset? since,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_PAGE_SIZE", "pageSize must be between 1 and 100."));
        }

        if (!legalEntityId.HasValue && Request.Query.ContainsKey("legalEntityId"))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_LEGAL_ENTITY_ID", "legalEntityId must be a valid GUID format."));
        }

        ReconciliationStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ReconciliationStatus>(status, true, out var statusValue))
            {
                return BadRequest(BuildError("VALIDATION.INVALID_STATUS", $"Unknown reconciliation status '{status}'."));
            }

            parsedStatus = statusValue;
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        if (legalEntityId.HasValue && !access.CanAccess(legalEntityId.Value))
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
            From = from,
            To = to,
            Since = since,
            Cursor = cursor,
            PageSize = pageSize
        }, cancellationToken);

        return Ok(new GetReconciliationExceptionsResponse
        {
            Data = result.Records.Select(MapException).ToList(),
            Meta = new ReconciliationExceptionPageMeta
            {
                PageSize = result.Records.Count,
                HasMore = result.HasMore,
                NextCursor = result.NextCursor,
                TotalCount = result.TotalCount
            }
        });
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ReconciliationRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var record = await _db.ReconciliationRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (record is null || !access.CanAccess(record.LegalEntityId))
        {
            return NotFound(BuildError("NOT_FOUND.RECONCILIATION", "Reconciliation record was not found."));
        }

        PreAuthRecord? preAuth = null;
        if (record.PreAuthId.HasValue)
        {
            preAuth = await _db.PreAuthRecords
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    item => item.Id == record.PreAuthId.Value
                        && item.LegalEntityId == record.LegalEntityId,
                    cancellationToken);
        }

        var transaction = await _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == record.TransactionId
                    && item.LegalEntityId == record.LegalEntityId,
                cancellationToken);

        return Ok(MapRecord(record, preAuth, transaction));
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
            return BadRequest(BuildError("VALIDATION.REASON_REQUIRED", "reason is required."));
        }

        var reason = request.Reason.Trim();
        if (reason.Length < ReviewReconciliationCommand.MinimumReasonLength)
        {
            return BadRequest(BuildError(
                "VALIDATION.REASON_TOO_SHORT",
                $"reason must be at least {ReviewReconciliationCommand.MinimumReasonLength} characters."));
        }

        if (reason.Length > ReviewReconciliationCommand.MaximumReasonLength)
        {
            return BadRequest(BuildError(
                "VALIDATION.REASON_TOO_LONG",
                $"reason must not exceed {ReviewReconciliationCommand.MaximumReasonLength} characters."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var reviewedBy = _accessResolver.ResolveUserId(User);
        if (string.IsNullOrWhiteSpace(reviewedBy))
        {
            return Forbid();
        }

        var result = await _mediator.Send(new ReviewReconciliationCommand
        {
            ReconciliationId = id,
            TargetStatus = targetStatus,
            Reason = reason,
            ReviewedByUserId = reviewedBy,
            ScopedLegalEntityIds = access.ScopedLegalEntityIds,
            AllowAllLegalEntities = access.AllowAllLegalEntities
        }, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "VALIDATION.REASON_REQUIRED" or "VALIDATION.REASON_TOO_SHORT" or "VALIDATION.REASON_TOO_LONG" or "VALIDATION.INVALID_STATUS" =>
                    BadRequest(BuildError(result.Error.Code, result.Error.Message)),
                "NOT_FOUND.RECONCILIATION" =>
                    NotFound(BuildError(result.Error.Code, result.Error.Message)),
                "FORBIDDEN.LEGAL_ENTITY_SCOPE" =>
                    Forbid(),
                "CONFLICT.INVALID_TRANSITION" or "CONFLICT.RACE_CONDITION" =>
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

    private static ReconciliationExceptionDto MapException(ReconciliationExceptionListItem item)
    {
        var decision = item.Status switch
        {
            ReconciliationStatus.APPROVED => "APPROVED",
            ReconciliationStatus.REJECTED => "REJECTED",
            _ => null
        };

        return new ReconciliationExceptionDto
        {
            Id = item.ReconciliationId,
            ReconciliationId = item.ReconciliationId,
            PreAuthId = item.PreAuthId,
            TransactionId = item.TransactionId,
            Status = item.Status.ToString(),
            SiteCode = item.SiteCode,
            LegalEntityId = item.LegalEntityId,
            PumpNumber = item.PumpNumber,
            NozzleNumber = item.NozzleNumber,
            OdooOrderId = item.OdooOrderId,
            CurrencyCode = item.CurrencyCode,
            RequestedAmount = item.RequestedAmount,
            ActualAmount = item.ActualAmountMinorUnits,
            AmountVariance = item.VarianceMinorUnits,
            VarianceBps = ConvertStoredVariancePercentToBps(item.VariancePercent),
            AuthorizedAmountMinorUnits = item.AuthorizedAmountMinorUnits,
            ActualAmountMinorUnits = item.ActualAmountMinorUnits,
            VarianceMinorUnits = item.VarianceMinorUnits,
            VariancePercent = item.VariancePercent,
            MatchMethod = item.MatchMethod,
            AmbiguityFlag = item.AmbiguityFlag,
            Decision = decision,
            DecisionReason = item.ReviewReason,
            DecidedBy = item.ReviewedByUserId,
            DecidedAt = item.ReviewedAtUtc,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            LastMatchAttemptAt = item.LastMatchAttemptAt
        };
    }

    private static ReconciliationRecordDto MapRecord(
        ReconciliationRecord record,
        PreAuthRecord? preAuth,
        Transaction? transaction)
    {
        var decision = record.Status switch
        {
            ReconciliationStatus.APPROVED => "APPROVED",
            ReconciliationStatus.REJECTED => "REJECTED",
            _ => null
        };

        return new ReconciliationRecordDto
        {
            Id = record.Id,
            PreAuthId = record.PreAuthId,
            TransactionId = record.TransactionId,
            SiteCode = record.SiteCode,
            LegalEntityId = record.LegalEntityId,
            OdooOrderId = record.OdooOrderId ?? preAuth?.OdooOrderId,
            PumpNumber = record.PumpNumber,
            NozzleNumber = record.NozzleNumber,
            ProductCode = preAuth?.ProductCode ?? transaction?.ProductCode,
            CurrencyCode = preAuth?.CurrencyCode ?? transaction?.CurrencyCode,
            RequestedAmount = record.AuthorizedAmountMinorUnits ?? preAuth?.RequestedAmountMinorUnits,
            ActualAmount = record.ActualAmountMinorUnits,
            AmountVariance = record.VarianceMinorUnits,
            VarianceBps = ConvertStoredVariancePercentToBps(record.VariancePercent),
            VariancePercent = record.VariancePercent,
            MatchMethod = record.MatchMethod,
            AmbiguityFlag = record.AmbiguityFlag,
            PreAuthStatus = preAuth?.Status.ToString(),
            ReconciliationStatus = record.Status.ToString(),
            Decision = decision,
            DecisionReason = record.ReviewReason,
            DecidedBy = record.ReviewedByUserId,
            DecidedAt = record.ReviewedAtUtc,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            PreAuthSummary = preAuth is null
                ? null
                : new ReconciliationPreAuthSummaryDto
                {
                    RequestedAt = preAuth.RequestedAt,
                    VehicleNumber = preAuth.VehicleNumber,
                    CustomerBusinessName = preAuth.CustomerBusinessName,
                    AttendantId = preAuth.AttendantId,
                    FccCorrelationId = preAuth.FccCorrelationId,
                    FccAuthorizationCode = preAuth.FccAuthorizationCode
                },
            TransactionSummary = transaction is null
                ? null
                : new ReconciliationTransactionSummaryDto
                {
                    FccTransactionId = transaction.FccTransactionId,
                    VolumeMicrolitres = transaction.VolumeMicrolitres,
                    StartedAt = transaction.StartedAt,
                    CompletedAt = transaction.CompletedAt
                }
        };
    }

    private static decimal? ConvertStoredVariancePercentToBps(decimal? variancePercent) =>
        variancePercent.HasValue
            ? decimal.Round(variancePercent.Value * 100m, 2)
            : null;
}

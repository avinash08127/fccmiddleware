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

        var query = _db.ReconciliationRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AsQueryable();

        if (legalEntityId.HasValue)
        {
            query = query.Where(item => item.LegalEntityId == legalEntityId.Value);
        }
        else if (!access.AllowAllLegalEntities)
        {
            query = query.Where(item => access.ScopedLegalEntityIds.Contains(item.LegalEntityId));
        }

        if (!string.IsNullOrWhiteSpace(siteCode))
        {
            query = query.Where(item => item.SiteCode == siteCode);
        }

        if (parsedStatus.HasValue)
        {
            query = query.Where(item => item.Status == parsedStatus.Value);
        }
        else
        {
            query = query.Where(item =>
                item.Status == ReconciliationStatus.VARIANCE_FLAGGED
                || item.Status == ReconciliationStatus.UNMATCHED);
        }

        var lowerBound = from ?? since;
        if (lowerBound.HasValue)
        {
            query = query.Where(item => item.CreatedAt >= lowerBound.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(item => item.CreatedAt <= to.Value);
        }

        if (PortalCursor.TryDecode(cursor, out var cursorTimestamp, out var cursorId))
        {
            query = query.Where(item =>
                item.CreatedAt > cursorTimestamp
                || (item.CreatedAt == cursorTimestamp && item.Id.CompareTo(cursorId) > 0));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var preAuthIds = rows.Where(item => item.PreAuthId.HasValue).Select(item => item.PreAuthId!.Value).Distinct().ToList();
        var transactionIds = rows.Select(item => item.TransactionId).Distinct().ToList();

        var preAuthById = await _db.PreAuthRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => preAuthIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var transactionById = await _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => transactionIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var data = rows.Select(item =>
        {
            preAuthById.TryGetValue(item.PreAuthId ?? Guid.Empty, out var preAuth);
            transactionById.TryGetValue(item.TransactionId, out var transaction);
            var decision = item.Status switch
            {
                ReconciliationStatus.APPROVED => "APPROVED",
                ReconciliationStatus.REJECTED => "REJECTED",
                _ => null
            };

            return new ReconciliationExceptionDto
            {
                Id = item.Id,
                ReconciliationId = item.Id,
                PreAuthId = item.PreAuthId,
                TransactionId = item.TransactionId,
                Status = item.Status.ToString(),
                SiteCode = item.SiteCode,
                LegalEntityId = item.LegalEntityId,
                PumpNumber = item.PumpNumber,
                NozzleNumber = item.NozzleNumber,
                OdooOrderId = item.OdooOrderId ?? preAuth?.OdooOrderId,
                CurrencyCode = preAuth?.CurrencyCode ?? transaction?.CurrencyCode,
                RequestedAmount = item.AuthorizedAmountMinorUnits ?? preAuth?.RequestedAmountMinorUnits,
                ActualAmount = item.ActualAmountMinorUnits,
                AmountVariance = item.VarianceMinorUnits,
                VarianceBps = item.VariancePercent.HasValue ? decimal.Round(item.VariancePercent.Value * 100m, 2) : null,
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
        }).ToList();

        string? nextCursor = null;
        if (hasMore && rows.Count > 0)
        {
            var last = rows[^1];
            nextCursor = PortalCursor.Encode(last.CreatedAt, last.Id);
        }

        return Ok(new GetReconciliationExceptionsResponse
        {
            Data = data,
            Meta = new ReconciliationExceptionPageMeta
            {
                PageSize = data.Count,
                HasMore = hasMore,
                NextCursor = nextCursor,
                TotalCount = totalCount
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

        if (record is null)
        {
            return NotFound(BuildError("NOT_FOUND.RECONCILIATION", "Reconciliation record was not found."));
        }

        if (!access.CanAccess(record.LegalEntityId))
        {
            return Forbid();
        }

        PreAuthRecord? preAuth = null;
        if (record.PreAuthId.HasValue)
        {
            preAuth = await _db.PreAuthRecords
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == record.PreAuthId.Value, cancellationToken);
        }

        var transaction = await _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == record.TransactionId, cancellationToken);

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
            VarianceBps = record.VariancePercent.HasValue ? decimal.Round(record.VariancePercent.Value * 100m, 2) : null,
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
}

using System.Linq.Expressions;
using FccMiddleware.Api.Portal;
using FccMiddleware.Application.Transactions;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Contracts.Transactions;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/ops/transactions")]
[Authorize(Policy = "PortalUser")]
public sealed class OpsTransactionsController : PortalControllerBase
{
    private readonly FccMiddlewareDbContext _db;
    private readonly IMediator _mediator;
    private readonly PortalAccessResolver _accessResolver;

    public OpsTransactionsController(
        FccMiddlewareDbContext db,
        IMediator mediator,
        PortalAccessResolver accessResolver)
    {
        _db = db;
        _mediator = mediator;
        _accessResolver = accessResolver;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PortalPagedResult<PortalTransactionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetTransactions(
        [FromQuery] Guid legalEntityId,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? siteCode = null,
        [FromQuery] string? status = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? productCode = null,
        [FromQuery] string? fccTransactionId = null,
        [FromQuery] string? odooOrderId = null,
        [FromQuery] string? fccVendor = null,
        [FromQuery] string? ingestionSource = null,
        [FromQuery] int? pumpNumber = null,
        [FromQuery] bool? isStale = null,
        [FromQuery] string? sortField = null,
        [FromQuery] string? sortOrder = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_PAGE_SIZE", "pageSize must be between 1 and 100."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.CanAccess(legalEntityId))
        {
            return ForbidOrUnauthorized(access);
        }

        if (!TryParseEnum(status, out TransactionStatus? parsedStatus))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_STATUS", $"Unknown transaction status '{status}'."));
        }

        if (!TryParseEnum(fccVendor, out FccVendor? parsedVendor))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_FCC_VENDOR", $"Unknown FCC vendor '{fccVendor}'."));
        }

        if (!TryParseEnum(ingestionSource, out IngestionSource? parsedIngestionSource))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_INGESTION_SOURCE", $"Unknown ingestion source '{ingestionSource}'."));
        }

        var skip = DecodeOffset(cursor);
        var descending = !string.Equals(sortOrder, "asc", StringComparison.OrdinalIgnoreCase);

        var query = _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.LegalEntityId == legalEntityId);

        if (!string.IsNullOrWhiteSpace(siteCode))
        {
            query = query.Where(item => item.SiteCode == siteCode);
        }

        if (parsedStatus.HasValue)
        {
            query = query.Where(item => item.Status == parsedStatus.Value);
        }

        if (from.HasValue)
        {
            query = query.Where(item => item.CompletedAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(item => item.CompletedAt <= to.Value);
        }

        if (!string.IsNullOrWhiteSpace(productCode))
        {
            query = query.Where(item => item.ProductCode == productCode);
        }

        if (!string.IsNullOrWhiteSpace(fccTransactionId))
        {
            query = query.Where(item => EF.Functions.ILike(item.FccTransactionId, $"%{fccTransactionId.Trim()}%"));
        }

        if (!string.IsNullOrWhiteSpace(odooOrderId))
        {
            query = query.Where(item => item.OdooOrderId != null && EF.Functions.ILike(item.OdooOrderId, $"%{odooOrderId.Trim()}%"));
        }

        if (parsedVendor.HasValue)
        {
            query = query.Where(item => item.FccVendor == parsedVendor.Value);
        }

        if (parsedIngestionSource.HasValue)
        {
            query = query.Where(item => item.IngestionSource == parsedIngestionSource.Value);
        }

        if (pumpNumber.HasValue)
        {
            query = query.Where(item => item.PumpNumber == pumpNumber.Value);
        }

        if (isStale == true)
        {
            query = query.Where(item => item.IsStale);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        query = ApplyOrdering(query, sortField, descending);

        var rows = await query
            .Skip(skip)
            .Take(pageSize + 1)
            .Select(item => new PortalTransactionDto
            {
                Id = item.Id,
                FccTransactionId = item.FccTransactionId,
                SiteCode = item.SiteCode,
                PumpNumber = item.PumpNumber,
                NozzleNumber = item.NozzleNumber,
                ProductCode = item.ProductCode,
                VolumeMicrolitres = item.VolumeMicrolitres,
                AmountMinorUnits = item.AmountMinorUnits,
                UnitPriceMinorPerLitre = item.UnitPriceMinorPerLitre,
                CurrencyCode = item.CurrencyCode,
                Status = item.Status.ToString(),
                ReconciliationStatus = item.ReconciliationStatus.HasValue ? item.ReconciliationStatus.Value.ToString() : null,
                IngestionSource = item.IngestionSource.ToString(),
                FccVendor = item.FccVendor.ToString(),
                StartedAt = item.StartedAt,
                CompletedAt = item.CompletedAt,
                IngestedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt,
                CorrelationId = item.CorrelationId,
                LegalEntityId = item.LegalEntityId,
                SchemaVersion = item.SchemaVersion,
                IsDuplicate = item.IsDuplicate,
                DuplicateOfId = item.DuplicateOfId,
                PreAuthId = item.PreAuthId,
                OdooOrderId = item.OdooOrderId,
                FiscalReceiptNumber = item.FiscalReceiptNumber,
                AttendantId = item.AttendantId,
                RawPayloadRef = item.RawPayloadRef,
                RawPayloadJson = null,
                IsStale = item.IsStale
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return Ok(new PortalPagedResult<PortalTransactionDto>
        {
            Data = rows,
            Meta = new PortalPageMeta
            {
                PageSize = rows.Count,
                HasMore = hasMore,
                NextCursor = hasMore ? EncodeOffset(skip + rows.Count) : null,
                TotalCount = totalCount
            }
        });
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PortalTransactionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTransactionById(Guid id, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var transaction = await _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (transaction is null)
        {
            return NotFound(BuildError("NOT_FOUND.TRANSACTION", "Transaction was not found."));
        }

        if (!access.CanAccess(transaction.LegalEntityId))
        {
            return Forbid();
        }

        return Ok(new PortalTransactionDto
        {
            Id = transaction.Id,
            FccTransactionId = transaction.FccTransactionId,
            SiteCode = transaction.SiteCode,
            PumpNumber = transaction.PumpNumber,
            NozzleNumber = transaction.NozzleNumber,
            ProductCode = transaction.ProductCode,
            VolumeMicrolitres = transaction.VolumeMicrolitres,
            AmountMinorUnits = transaction.AmountMinorUnits,
            UnitPriceMinorPerLitre = transaction.UnitPriceMinorPerLitre,
            CurrencyCode = transaction.CurrencyCode,
            Status = transaction.Status.ToString(),
            ReconciliationStatus = transaction.ReconciliationStatus.HasValue ? transaction.ReconciliationStatus.Value.ToString() : null,
            IngestionSource = transaction.IngestionSource.ToString(),
            FccVendor = transaction.FccVendor.ToString(),
            StartedAt = transaction.StartedAt,
            CompletedAt = transaction.CompletedAt,
            IngestedAt = transaction.CreatedAt,
            UpdatedAt = transaction.UpdatedAt,
            CorrelationId = transaction.CorrelationId,
            LegalEntityId = transaction.LegalEntityId,
            SchemaVersion = transaction.SchemaVersion,
            IsDuplicate = transaction.IsDuplicate,
            DuplicateOfId = transaction.DuplicateOfId,
            PreAuthId = transaction.PreAuthId,
            OdooOrderId = transaction.OdooOrderId,
            FiscalReceiptNumber = transaction.FiscalReceiptNumber,
            AttendantId = transaction.AttendantId,
            RawPayloadRef = transaction.RawPayloadRef,
            RawPayloadJson = null,
            IsStale = transaction.IsStale
        });
    }

    [HttpPost("acknowledge")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(AcknowledgeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Acknowledge(
        [FromBody] AcknowledgeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Acknowledgements is { Count: 0 })
        {
            return BadRequest(BuildError("VALIDATION.EMPTY_BATCH", "Acknowledgements must contain at least one item."));
        }

        if (request.Acknowledgements.Count > 500)
        {
            return BadRequest(BuildError("VALIDATION.BATCH_TOO_LARGE", $"Batch size {request.Acknowledgements.Count} exceeds maximum of 500."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var ids = request.Acknowledgements.Select(item => item.Id).Distinct().ToList();
        var legalEntityIds = await _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => ids.Contains(item.Id))
            .Select(item => item.LegalEntityId)
            .Distinct()
            .ToListAsync(cancellationToken);

        Guid legalEntityId;
        if (legalEntityIds.Count == 1)
        {
            legalEntityId = legalEntityIds[0];
        }
        else if (legalEntityIds.Count == 0 && access.ScopedLegalEntityIds.Count == 1)
        {
            legalEntityId = access.ScopedLegalEntityIds.Single();
        }
        else
        {
            return BadRequest(BuildError(
                "VALIDATION.ACKNOWLEDGE_SCOPE_REQUIRED",
                "Acknowledgements must target a single accessible legal entity."));
        }

        if (!access.CanAccess(legalEntityId))
        {
            return Forbid();
        }

        var result = await _mediator.Send(new AcknowledgeTransactionsBatchCommand
        {
            LegalEntityId = legalEntityId,
            Items = request.Acknowledgements.Select(item => new AcknowledgeTransactionItem
            {
                TransactionId = item.Id,
                OdooOrderId = item.OdooOrderId
            }).ToList()
        }, cancellationToken);

        return Ok(new AcknowledgeResponse
        {
            Results = result.Results.Select(item => new AcknowledgeResult
            {
                Id = item.TransactionId,
                Outcome = item.Outcome.ToString(),
                Error = item.ErrorCode is null ? null : new AcknowledgeError
                {
                    Code = item.ErrorCode,
                    Message = item.ErrorMessage ?? string.Empty
                }
            }).ToList(),
            SucceededCount = result.SucceededCount,
            FailedCount = result.FailedCount
        });
    }

    private static bool TryParseEnum<TEnum>(string? raw, out TEnum? value)
        where TEnum : struct, Enum
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (Enum.TryParse<TEnum>(raw, true, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static IQueryable<Transaction> ApplyOrdering(IQueryable<Transaction> query, string? sortField, bool descending)
    {
        var field = sortField?.Trim();

        return field switch
        {
            "fccTransactionId" => ThenById(OrderBy(query, item => item.FccTransactionId, descending), descending),
            "siteCode" => ThenById(OrderBy(query, item => item.SiteCode, descending), descending),
            "volumeMicrolitres" => ThenById(OrderBy(query, item => item.VolumeMicrolitres, descending), descending),
            "amountMinorUnits" => ThenById(OrderBy(query, item => item.AmountMinorUnits, descending), descending),
            "status" => ThenById(OrderBy(query, item => item.Status, descending), descending),
            "startedAt" => ThenById(OrderBy(query, item => item.StartedAt, descending), descending),
            _ => ThenById(OrderBy(query, item => item.CompletedAt, descending), descending)
        };
    }

    private static IOrderedQueryable<Transaction> OrderBy<TKey>(
        IQueryable<Transaction> query,
        Expression<Func<Transaction, TKey>> selector,
        bool descending) =>
        descending ? query.OrderByDescending(selector) : query.OrderBy(selector);

    private static IOrderedQueryable<Transaction> ThenById(
        IOrderedQueryable<Transaction> query,
        bool descending) =>
        descending ? query.ThenByDescending(item => item.Id) : query.ThenBy(item => item.Id);

    private static int DecodeOffset(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };

            var raw = Convert.FromBase64String(padded);
            return int.TryParse(System.Text.Encoding.UTF8.GetString(raw), out var offset) && offset >= 0
                ? offset
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string EncodeOffset(int offset) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(offset.ToString()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

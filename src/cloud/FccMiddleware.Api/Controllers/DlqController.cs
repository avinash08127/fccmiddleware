using FccMiddleware.Api.Portal;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/dlq")]
[Authorize(Policy = "PortalUser")]
public sealed class DlqController : PortalControllerBase
{
    private readonly FccMiddlewareDbContext _db;
    private readonly PortalAccessResolver _accessResolver;

    public DlqController(FccMiddlewareDbContext db, PortalAccessResolver accessResolver)
    {
        _db = db;
        _accessResolver = accessResolver;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PortalPagedResult<DeadLetterDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDeadLetters(
        [FromQuery] Guid legalEntityId,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? siteCode = null,
        [FromQuery] string? failureReason = null,
        [FromQuery] string? status = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_PAGE_SIZE", "pageSize must be between 1 and 100."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        if (!access.CanAccess(legalEntityId))
        {
            return Forbid();
        }

        if (!TryParseReason(failureReason, out var parsedReason))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_FAILURE_REASON", $"Unknown failure reason '{failureReason}'."));
        }

        if (!TryParseStatus(status, out var parsedStatus))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_STATUS", $"Unknown dead-letter status '{status}'."));
        }

        var query = _db.DeadLetterItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.LegalEntityId == legalEntityId);

        if (!string.IsNullOrWhiteSpace(siteCode))
        {
            query = query.Where(item => item.SiteCode == siteCode);
        }

        if (parsedReason.HasValue)
        {
            query = query.Where(item => item.FailureReason == parsedReason.Value);
        }

        if (parsedStatus.HasValue)
        {
            query = query.Where(item => item.Status == parsedStatus.Value);
        }

        if (from.HasValue)
        {
            query = query.Where(item => item.CreatedAt >= from.Value);
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

        var totalCount = await _db.DeadLetterItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.LegalEntityId == legalEntityId)
            .Where(item => string.IsNullOrWhiteSpace(siteCode) || item.SiteCode == siteCode)
            .Where(item => !parsedReason.HasValue || item.FailureReason == parsedReason.Value)
            .Where(item => !parsedStatus.HasValue || item.Status == parsedStatus.Value)
            .Where(item => !from.HasValue || item.CreatedAt >= from.Value)
            .Where(item => !to.HasValue || item.CreatedAt <= to.Value)
            .CountAsync(cancellationToken);

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

        string? nextCursor = null;
        if (hasMore && rows.Count > 0)
        {
            var last = rows[^1];
            nextCursor = PortalCursor.Encode(last.CreatedAt, last.Id);
        }

        return Ok(new PortalPagedResult<DeadLetterDto>
        {
            Data = rows.Select(MapDeadLetter).ToList(),
            Meta = new PortalPageMeta
            {
                PageSize = rows.Count,
                HasMore = hasMore,
                NextCursor = nextCursor,
                TotalCount = totalCount
            }
        });
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DeadLetterDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var item = await _db.DeadLetterItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (item is null)
        {
            return NotFound(BuildError("NOT_FOUND.DEAD_LETTER", "Dead-letter item was not found."));
        }

        if (!access.CanAccess(item.LegalEntityId))
        {
            return Forbid();
        }

        return Ok(MapDeadLetterDetail(item));
    }

    [HttpPost("{id:guid}/retry")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(RetryResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var item = await _db.DeadLetterItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (item is null)
        {
            return NotFound(BuildError("NOT_FOUND.DEAD_LETTER", "Dead-letter item was not found."));
        }

        if (!access.CanAccess(item.LegalEntityId))
        {
            return Forbid();
        }

        ApplyRetry(item, "SUCCESS", null, null);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new RetryResultDto
        {
            Id = item.Id,
            Queued = true,
            Error = null
        });
    }

    [HttpPost("{id:guid}/discard")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discard(
        Guid id,
        [FromBody] DiscardRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var item = await _db.DeadLetterItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (item is null)
        {
            return NotFound(BuildError("NOT_FOUND.DEAD_LETTER", "Dead-letter item was not found."));
        }

        if (!access.CanAccess(item.LegalEntityId))
        {
            return Forbid();
        }

        item.Status = DeadLetterStatus.DISCARDED;
        item.DiscardReason = request.Reason;
        item.DiscardedBy = _accessResolver.ResolveUserId(User);
        item.DiscardedAt = DateTimeOffset.UtcNow;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok();
    }

    [HttpPost("retry-batch")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(BatchRetryResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RetryBatch(
        [FromBody] RetryBatchRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var rows = await _db.DeadLetterItems
            .IgnoreQueryFilters()
            .Where(item => request.Ids.Contains(item.Id))
            .ToListAsync(cancellationToken);

        var succeeded = new List<Guid>();
        var failed = new List<BatchRetryFailureDto>();

        foreach (var id in request.Ids)
        {
            var row = rows.FirstOrDefault(item => item.Id == id);
            if (row is null)
            {
                failed.Add(new BatchRetryFailureDto { Id = id, Error = "Not found." });
                continue;
            }

            if (!access.CanAccess(row.LegalEntityId))
            {
                failed.Add(new BatchRetryFailureDto { Id = id, Error = "Forbidden." });
                continue;
            }

            ApplyRetry(row, "SUCCESS", null, null);
            succeeded.Add(id);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new BatchRetryResultDto
        {
            Succeeded = succeeded,
            Failed = failed
        });
    }

    [HttpPost("discard-batch")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DiscardBatch(
        [FromBody] DiscardBatchRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var rows = await _db.DeadLetterItems
            .IgnoreQueryFilters()
            .Where(item => request.Items.Select(batchItem => batchItem.Id).Contains(item.Id))
            .ToListAsync(cancellationToken);

        foreach (var batchItem in request.Items)
        {
            var row = rows.FirstOrDefault(item => item.Id == batchItem.Id);
            if (row is null || !access.CanAccess(row.LegalEntityId))
            {
                continue;
            }

            row.Status = DeadLetterStatus.DISCARDED;
            row.DiscardReason = batchItem.Reason;
            row.DiscardedBy = _accessResolver.ResolveUserId(User);
            row.DiscardedAt = DateTimeOffset.UtcNow;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    private void ApplyRetry(DeadLetterItem item, string outcome, string? errorCode, string? errorMessage)
    {
        var history = DeserializeHistory(item);
        history.Add(new RetryHistoryEntryDto
        {
            AttemptNumber = history.Count + 1,
            AttemptedAt = DateTimeOffset.UtcNow,
            Outcome = outcome,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        });

        item.RetryCount += 1;
        item.LastRetryAt = DateTimeOffset.UtcNow;
        item.Status = DeadLetterStatus.RETRYING;
        item.RetryHistoryJson = JsonSerializer.Serialize(history, PortalJson.SerializerOptions);
        item.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static List<RetryHistoryEntryDto> DeserializeHistory(DeadLetterItem item) =>
        string.IsNullOrWhiteSpace(item.RetryHistoryJson)
            ? []
            : JsonSerializer.Deserialize<List<RetryHistoryEntryDto>>(item.RetryHistoryJson, PortalJson.SerializerOptions) ?? [];

    private static DeadLetterDto MapDeadLetter(DeadLetterItem item) =>
        new()
        {
            Id = item.Id,
            Type = item.Type.ToString(),
            SiteCode = item.SiteCode,
            LegalEntityId = item.LegalEntityId,
            FccTransactionId = item.FccTransactionId,
            RawPayloadRef = item.RawPayloadRef,
            FailureReason = item.FailureReason.ToString(),
            ErrorCode = item.ErrorCode,
            ErrorMessage = item.ErrorMessage,
            Status = item.Status.ToString(),
            RetryCount = item.RetryCount,
            LastRetryAt = item.LastRetryAt,
            DiscardReason = item.DiscardReason,
            DiscardedBy = item.DiscardedBy,
            DiscardedAt = item.DiscardedAt,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };

    private static DeadLetterDetailDto MapDeadLetterDetail(DeadLetterItem item) =>
        new()
        {
            Id = item.Id,
            Type = item.Type.ToString(),
            SiteCode = item.SiteCode,
            LegalEntityId = item.LegalEntityId,
            FccTransactionId = item.FccTransactionId,
            RawPayloadRef = item.RawPayloadRef,
            FailureReason = item.FailureReason.ToString(),
            ErrorCode = item.ErrorCode,
            ErrorMessage = item.ErrorMessage,
            Status = item.Status.ToString(),
            RetryCount = item.RetryCount,
            LastRetryAt = item.LastRetryAt,
            DiscardReason = item.DiscardReason,
            DiscardedBy = item.DiscardedBy,
            DiscardedAt = item.DiscardedAt,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            RawPayload = string.IsNullOrWhiteSpace(item.RawPayloadJson) ? null : PortalJson.ParseJson(item.RawPayloadJson),
            RetryHistory = DeserializeHistory(item)
        };

    private static bool TryParseReason(string? value, out DeadLetterReason? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<DeadLetterReason>(value, true, out var parsed))
        {
            reason = parsed;
            return true;
        }

        return false;
    }

    private static bool TryParseStatus(string? value, out DeadLetterStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<DeadLetterStatus>(value, true, out var parsed))
        {
            status = parsed;
            return true;
        }

        return false;
    }
}

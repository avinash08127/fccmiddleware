using FccMiddleware.Api.Portal;
using FccMiddleware.Application.DeadLetter;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text.Json;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/dlq")]
[Authorize(Policy = "PortalUser")]
public sealed class DlqController : PortalControllerBase
{
    private readonly FccMiddlewareDbContext _db;
    private readonly PortalAccessResolver _accessResolver;
    private readonly IDlqReplayService _replayService;

    public DlqController(
        FccMiddlewareDbContext db,
        PortalAccessResolver accessResolver,
        IDlqReplayService replayService)
    {
        _db = db;
        _accessResolver = accessResolver;
        _replayService = replayService;
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
            .ForPortal(access, legalEntityId);

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

        var totalCount = await query.CountAsync(cancellationToken);

        if (PortalCursor.TryDecode(cursor, out var cursorTimestamp, out var cursorId))
        {
            query = query.Where(item =>
                item.CreatedAt > cursorTimestamp
                || (item.CreatedAt == cursorTimestamp && item.Id.CompareTo(cursorId) > 0));
        }

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

        var canViewSensitive = PortalAccessResolver.HasSensitiveDataAccess(User);
        return Ok(MapDeadLetterDetail(item, includeSensitivePayload: canViewSensitive));
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

        if (item.Status is DeadLetterStatus.RESOLVED or DeadLetterStatus.DISCARDED)
        {
            return BadRequest(BuildError("DLQ.INVALID_STATE", $"Cannot retry item in {item.Status} state."));
        }

        var result = await _replayService.ReplayAsync(id, cancellationToken);

        return Ok(new RetryResultDto
        {
            Id = item.Id,
            Queued = result.Success,
            Error = result.Success ? null : result.ErrorMessage
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

        if (item.Status is DeadLetterStatus.RESOLVED or DeadLetterStatus.DISCARDED)
        {
            return BadRequest(BuildError("DLQ.INVALID_STATE", $"Cannot discard item in {item.Status} state."));
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 10)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_REASON", "Discard reason must be at least 10 characters."));
        }

        var now = DateTimeOffset.UtcNow;
        var userId = _accessResolver.ResolveUserId(User);

        item.Status = DeadLetterStatus.DISCARDED;
        item.DiscardReason = request.Reason;
        item.DiscardedBy = userId;
        item.DiscardedAt = now;
        item.UpdatedAt = now;

        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = item.LegalEntityId,
            EventType = "DEAD_LETTER_DISCARDED",
            CorrelationId = Guid.NewGuid(),
            SiteCode = item.SiteCode,
            Source = "DlqController",
            Payload = JsonSerializer.Serialize(new
            {
                DeadLetterId = item.Id,
                item.SiteCode,
                Reason = request.Reason,
                DiscardedBy = userId,
            }),
            EntityId = item.Id
        });

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
        if (request.Ids.Count is 0 or > 50)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_BATCH_SIZE", "Batch size must be between 1 and 50."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var rows = await _db.DeadLetterItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => request.Ids.Contains(item.Id))
            .ToListAsync(cancellationToken);

        var succeeded = new ConcurrentBag<Guid>();
        var failed = new ConcurrentBag<BatchRetryFailureDto>();
        var toReplay = new List<Guid>();

        // Validate access and state synchronously before dispatching replay tasks.
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

            if (row.Status is DeadLetterStatus.RESOLVED or DeadLetterStatus.DISCARDED)
            {
                failed.Add(new BatchRetryFailureDto { Id = id, Error = $"Cannot retry item in {row.Status} state." });
                continue;
            }

            toReplay.Add(id);
        }

        // Replay eligible items in parallel with bounded concurrency.
        using var semaphore = new SemaphoreSlim(5);
        var replayTasks = toReplay.Select(async id =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var result = await _replayService.ReplayAsync(id, cancellationToken);
                if (result.Success)
                    succeeded.Add(id);
                else
                    failed.Add(new BatchRetryFailureDto { Id = id, Error = result.ErrorMessage ?? "Replay failed." });
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(replayTasks);

        return Ok(new BatchRetryResultDto
        {
            Succeeded = [.. succeeded],
            Failed = [.. failed]
        });
    }

    [HttpPost("discard-batch")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(BatchDiscardResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DiscardBatch(
        [FromBody] DiscardBatchRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (request.Items.Count is 0 or > 50)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_BATCH_SIZE", "Batch size must be between 1 and 50."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var rows = await _db.DeadLetterItems
            .IgnoreQueryFilters()
            .Where(item => request.Items.Select(batchItem => batchItem.Id).Contains(item.Id))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var userId = _accessResolver.ResolveUserId(User);
        var succeeded = new List<Guid>();
        var failed = new List<BatchDiscardFailureDto>();

        foreach (var batchItem in request.Items)
        {
            var row = rows.FirstOrDefault(item => item.Id == batchItem.Id);
            if (row is null)
            {
                failed.Add(new BatchDiscardFailureDto { Id = batchItem.Id, Error = "Not found." });
                continue;
            }

            if (!access.CanAccess(row.LegalEntityId))
            {
                failed.Add(new BatchDiscardFailureDto { Id = batchItem.Id, Error = "Forbidden." });
                continue;
            }

            if (row.Status is DeadLetterStatus.RESOLVED or DeadLetterStatus.DISCARDED)
            {
                failed.Add(new BatchDiscardFailureDto { Id = batchItem.Id, Error = $"Cannot discard item in {row.Status} state." });
                continue;
            }

            if (string.IsNullOrWhiteSpace(batchItem.Reason) || batchItem.Reason.Trim().Length < 10)
            {
                failed.Add(new BatchDiscardFailureDto { Id = batchItem.Id, Error = "Reason must be at least 10 characters." });
                continue;
            }

            row.Status = DeadLetterStatus.DISCARDED;
            row.DiscardReason = batchItem.Reason;
            row.DiscardedBy = userId;
            row.DiscardedAt = now;
            row.UpdatedAt = now;

            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                LegalEntityId = row.LegalEntityId,
                EventType = "DEAD_LETTER_DISCARDED",
                CorrelationId = Guid.NewGuid(),
                SiteCode = row.SiteCode,
                Source = "DlqController",
                Payload = JsonSerializer.Serialize(new
                {
                    DeadLetterId = row.Id,
                    row.SiteCode,
                    Reason = batchItem.Reason,
                    DiscardedBy = userId,
                }),
                EntityId = row.Id
            });

            succeeded.Add(row.Id);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new BatchDiscardResultDto
        {
            Succeeded = succeeded,
            Failed = failed
        });
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

    private static DeadLetterDetailDto MapDeadLetterDetail(DeadLetterItem item, bool includeSensitivePayload = false) =>
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
            RawPayload = includeSensitivePayload && !string.IsNullOrWhiteSpace(item.RawPayloadJson)
                ? PortalJson.ParseJson(item.RawPayloadJson)
                : null,
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

using FccMiddleware.Api.Portal;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/audit/events")]
[Authorize(Policy = "PortalUser")]
public sealed class AuditController : PortalControllerBase
{
    private readonly FccMiddlewareDbContext _db;
    private readonly PortalAccessResolver _accessResolver;

    public AuditController(FccMiddlewareDbContext db, PortalAccessResolver accessResolver)
    {
        _db = db;
        _accessResolver = accessResolver;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PortalPagedResult<AuditEventDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAuditEvents(
        [FromQuery] Guid legalEntityId,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? correlationId = null,
        [FromQuery] List<string>? eventTypes = null,
        [FromQuery] string? siteCode = null,
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

        var query = _db.AuditEvents
            .ForPortal(access, legalEntityId);

        if (correlationId.HasValue)
        {
            query = query.Where(item => item.CorrelationId == correlationId.Value);
        }

        if (eventTypes is { Count: > 0 })
        {
            query = query.Where(item => eventTypes.Contains(item.EventType));
        }

        if (!string.IsNullOrWhiteSpace(siteCode))
        {
            query = query.Where(item => item.SiteCode == siteCode);
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

        var canViewSensitive = PortalAccessResolver.HasSensitiveDataAccess(User);
        var data = rows.Select(row => ToAuditEventDto(row, canViewSensitive)).ToList();

        return Ok(new PortalPagedResult<AuditEventDto>
        {
            Data = data,
            Meta = new PortalPageMeta
            {
                PageSize = data.Count,
                HasMore = hasMore,
                NextCursor = nextCursor,
                TotalCount = totalCount
            }
        });
    }

    [HttpGet("{eventId:guid}")]
    [ProducesResponseType(typeof(AuditEventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAuditEventById(Guid eventId, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var auditEvent = await _db.AuditEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);

        if (auditEvent is null)
        {
            return NotFound(BuildError("NOT_FOUND.AUDIT_EVENT", "Audit event was not found."));
        }

        if (!access.CanAccess(auditEvent.LegalEntityId))
        {
            return Forbid();
        }

        var canViewSensitive = PortalAccessResolver.HasSensitiveDataAccess(User);
        return Ok(ToAuditEventDto(auditEvent, canViewSensitive));
    }

    private static AuditEventDto ToAuditEventDto(Domain.Entities.AuditEvent auditEvent, bool includePayload)
    {
        var payload = PortalJson.ParseJson(auditEvent.Payload);

        return new AuditEventDto
        {
            EventId = PortalJson.ReadEventId(auditEvent, payload),
            EventType = auditEvent.EventType,
            SchemaVersion = PortalJson.ReadSchemaVersion(payload),
            Timestamp = PortalJson.ReadTimestamp(auditEvent, payload),
            Source = auditEvent.Source,
            CorrelationId = auditEvent.CorrelationId,
            LegalEntityId = auditEvent.LegalEntityId,
            SiteCode = auditEvent.SiteCode,
            Payload = includePayload ? payload : default
        };
    }
}

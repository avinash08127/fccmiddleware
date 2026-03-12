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
[Route("api/v1/agents")]
[Authorize(Policy = "PortalUser")]
public sealed class AgentsController : PortalControllerBase
{
    private readonly FccMiddlewareDbContext _db;
    private readonly PortalAccessResolver _accessResolver;

    public AgentsController(FccMiddlewareDbContext db, PortalAccessResolver accessResolver)
    {
        _db = db;
        _accessResolver = accessResolver;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PortalPagedResult<AgentHealthSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAgents(
        [FromQuery] Guid legalEntityId,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? siteCode = null,
        [FromQuery] string? status = null,
        [FromQuery] string? connectivityState = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 500)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_PAGE_SIZE", "pageSize must be between 1 and 500."));
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

        if (!TryParseAgentStatus(status, out var activeFilter))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_STATUS", $"Unknown agent status '{status}'."));
        }

        if (!TryParseConnectivityState(connectivityState, out var connectivityFilter))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_CONNECTIVITY_STATE", $"Unknown connectivity state '{connectivityState}'."));
        }

        var query = _db.AgentRegistrations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(agent => agent.Site)
            .Where(agent => agent.LegalEntityId == legalEntityId);

        if (!string.IsNullOrWhiteSpace(siteCode))
        {
            query = query.Where(agent => agent.SiteCode == siteCode);
        }

        if (activeFilter.HasValue)
        {
            query = query.Where(agent => agent.IsActive == activeFilter.Value);
        }

        if (PortalCursor.TryDecode(cursor, out var cursorTimestamp, out var cursorId))
        {
            query = query.Where(agent =>
                agent.RegisteredAt > cursorTimestamp
                || (agent.RegisteredAt == cursorTimestamp && agent.Id.CompareTo(cursorId) > 0));
        }

        var orderedQuery = query
            .OrderBy(agent => agent.RegisteredAt)
            .ThenBy(agent => agent.Id);

        var totalCount = await _db.AgentRegistrations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(agent => agent.LegalEntityId == legalEntityId)
            .Where(agent => string.IsNullOrWhiteSpace(siteCode) || agent.SiteCode == siteCode)
            .Where(agent => !activeFilter.HasValue || agent.IsActive == activeFilter.Value)
            .CountAsync(cancellationToken);

        var page = await orderedQuery
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > pageSize;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        var snapshots = await _db.AgentTelemetrySnapshots
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(snapshot => snapshot.LegalEntityId == legalEntityId && page.Select(agent => agent.Id).Contains(snapshot.DeviceId))
            .ToDictionaryAsync(snapshot => snapshot.DeviceId, cancellationToken);

        var data = page
            .Select(agent =>
            {
                snapshots.TryGetValue(agent.Id, out var snapshot);
                return new AgentHealthSummaryDto
                {
                    DeviceId = agent.Id,
                    SiteCode = agent.SiteCode,
                    SiteName = agent.Site.SiteName,
                    LegalEntityId = agent.LegalEntityId,
                    AgentVersion = agent.AgentVersion,
                    Status = agent.IsActive ? "ACTIVE" : "DEACTIVATED",
                    ConnectivityState = snapshot?.ConnectivityState.ToString(),
                    BatteryPercent = snapshot?.BatteryPercent,
                    IsCharging = snapshot?.IsCharging,
                    BufferDepth = snapshot?.PendingUploadCount,
                    SyncLagSeconds = snapshot?.SyncLagSeconds,
                    LastTelemetryAt = snapshot?.ReportedAtUtc,
                    LastSeenAt = agent.LastSeenAt
                };
            })
            .Where(dto => connectivityFilter is null || string.Equals(dto.ConnectivityState, connectivityFilter.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? nextCursor = null;
        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            nextCursor = PortalCursor.Encode(last.RegisteredAt, last.Id);
        }

        return Ok(new PortalPagedResult<AgentHealthSummaryDto>
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

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AgentRegistrationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAgentById(Guid id, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var agent = await _db.AgentRegistrations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (agent is null)
        {
            return NotFound(BuildError("NOT_FOUND.AGENT", "Agent was not found."));
        }

        if (!access.CanAccess(agent.LegalEntityId))
        {
            return Forbid();
        }

        return Ok(ToAgentRegistrationDto(agent));
    }

    [HttpGet("{id:guid}/telemetry")]
    [ProducesResponseType(typeof(AgentTelemetryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTelemetry(Guid id, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var agent = await _db.AgentRegistrations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (agent is null)
        {
            return NotFound(BuildError("NOT_FOUND.AGENT", "Agent was not found."));
        }

        if (!access.CanAccess(agent.LegalEntityId))
        {
            return Forbid();
        }

        var snapshot = await _db.AgentTelemetrySnapshots
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.DeviceId == id, cancellationToken);

        if (snapshot is null)
        {
            return NotFound(BuildError("NOT_FOUND.TELEMETRY", "Telemetry has not been reported for this agent."));
        }

        var telemetry = JsonSerializer.Deserialize<AgentTelemetryDto>(snapshot.PayloadJson, PortalJson.SerializerOptions);
        if (telemetry is null)
        {
            return NotFound(BuildError("NOT_FOUND.TELEMETRY", "Telemetry payload could not be read."));
        }

        return Ok(telemetry);
    }

    [HttpGet("{id:guid}/events")]
    [ProducesResponseType(typeof(IReadOnlyList<AgentAuditEventDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEvents(
        Guid id,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_LIMIT", "limit must be between 1 and 100."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var agent = await _db.AgentRegistrations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (agent is null)
        {
            return NotFound(BuildError("NOT_FOUND.AGENT", "Agent was not found."));
        }

        if (!access.CanAccess(agent.LegalEntityId))
        {
            return Forbid();
        }

        var rows = await _db.AuditEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.LegalEntityId == agent.LegalEntityId && item.SiteCode == agent.SiteCode)
            .Where(item =>
                item.EventType.StartsWith("Agent")
                || item.EventType == "ConnectivityChanged"
                || item.EventType == "BufferThresholdExceeded")
            .OrderByDescending(item => item.CreatedAt)
            .Take(limit * 10)
            .ToListAsync(cancellationToken);

        var events = rows
            .Select(item =>
            {
                var payload = PortalJson.ParseJson(item.Payload);
                return new { Item = item, Payload = payload };
            })
            .Where(item => PortalJson.TryReadDeviceId(item.Payload, out var deviceId) && deviceId == id)
            .Take(limit)
            .Select(item => new AgentAuditEventDto
            {
                Id = item.Item.Id,
                DeviceId = id,
                EventType = item.Item.EventType,
                Description = PortalJson.TryReadString(item.Payload, "message")
                    ?? PortalJson.TryReadString(item.Payload, "payload", "message")
                    ?? item.Item.EventType,
                PreviousState = PortalJson.TryReadString(item.Payload, "previousState")
                    ?? PortalJson.TryReadString(item.Payload, "payload", "previousState"),
                NewState = PortalJson.TryReadString(item.Payload, "newState")
                    ?? PortalJson.TryReadString(item.Payload, "payload", "newState"),
                OccurredAtUtc = PortalJson.ReadTimestamp(item.Item, item.Payload),
                Metadata = item.Payload
            })
            .ToList();

        return Ok(events);
    }

    private static AgentRegistrationDto ToAgentRegistrationDto(AgentRegistration agent) =>
        new()
        {
            Id = agent.Id,
            DeviceId = agent.Id,
            SiteCode = agent.SiteCode,
            LegalEntityId = agent.LegalEntityId,
            DeviceSerialNumber = agent.DeviceSerialNumber,
            DeviceModel = agent.DeviceModel,
            OsVersion = agent.OsVersion,
            AgentVersion = agent.AgentVersion,
            Status = agent.IsActive ? "ACTIVE" : "DEACTIVATED",
            RegisteredAt = agent.RegisteredAt,
            LastSeenAt = agent.LastSeenAt
        };

    private static bool TryParseAgentStatus(string? status, out bool? isActive)
    {
        isActive = null;
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        if (string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            isActive = true;
            return true;
        }

        if (string.Equals(status, "DEACTIVATED", StringComparison.OrdinalIgnoreCase))
        {
            isActive = false;
            return true;
        }

        return false;
    }

    private static bool TryParseConnectivityState(string? value, out ConnectivityState? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<ConnectivityState>(value, true, out var parsed))
        {
            state = parsed;
            return true;
        }

        return false;
    }
}

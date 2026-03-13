using FccMiddleware.Api.Portal;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Models;
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
        if (legalEntityId == Guid.Empty)
        {
            return BadRequest(BuildError("VALIDATION.LEGAL_ENTITY_REQUIRED", "legalEntityId is required."));
        }

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

        // baseQuery holds all filter predicates but NO cursor predicate.
        IQueryable<AgentRegistration> baseQuery = _db.AgentRegistrations
            .ForPortal(access, legalEntityId)
            .Include(agent => agent.Site);

        if (!string.IsNullOrWhiteSpace(siteCode))
        {
            baseQuery = baseQuery.Where(agent => agent.SiteCode.Contains(siteCode));
        }

        if (activeFilter.HasValue)
        {
            baseQuery = baseQuery.Where(agent => agent.IsActive == activeFilter.Value);
        }

        // Push connectivity filter into the SQL query by joining with telemetry snapshots,
        // so pagination is correct when filtering by connectivity state.
        if (connectivityFilter.HasValue)
        {
            baseQuery = baseQuery.Where(agent =>
                _db.AgentTelemetrySnapshots
                    .IgnoreQueryFilters()
                    .Any(snapshot => snapshot.DeviceId == agent.Id
                                    && snapshot.ConnectivityState == connectivityFilter.Value));
        }

        // pageQuery extends baseQuery with the cursor predicate (page-fetch only).
        IQueryable<AgentRegistration> pageQuery = baseQuery;
        if (PortalCursor.TryDecode(cursor, out var cursorTimestamp, out var cursorId))
        {
            pageQuery = baseQuery.Where(agent =>
                agent.RegisteredAt > cursorTimestamp
                || (agent.RegisteredAt == cursorTimestamp && agent.Id.CompareTo(cursorId) > 0));
        }

        var page = await pageQuery
            .OrderBy(agent => agent.RegisteredAt)
            .ThenBy(agent => agent.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > pageSize;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        // FM-T02: Extract IDs to an array so Npgsql translates to = ANY(@p)
        // instead of generating N literal GUID parameters in the SQL.
        var pageIds = page.Select(agent => agent.Id).ToArray();
        var snapshots = await _db.AgentTelemetrySnapshots
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(snapshot => snapshot.LegalEntityId == legalEntityId && pageIds.Contains(snapshot.DeviceId))
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
                    HasTelemetry = snapshot is not null,
                    ConnectivityState = snapshot?.ConnectivityState.ToString(),
                    BatteryPercent = snapshot?.BatteryPercent,
                    IsCharging = snapshot?.IsCharging,
                    BufferDepth = snapshot?.PendingUploadCount,
                    SyncLagSeconds = snapshot?.SyncLagSeconds,
                    LastTelemetryAt = snapshot?.ReportedAtUtc,
                    LastSeenAt = agent.LastSeenAt
                };
            })
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
                TotalCount = null
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
            .ForPortal(access)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (agent is null)
        {
            return NotFound(BuildError("NOT_FOUND.AGENT", "Agent was not found."));
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
            .ForPortal(access)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (agent is null)
        {
            return NotFound(BuildError("NOT_FOUND.AGENT", "Agent was not found."));
        }

        var snapshot = await _db.AgentTelemetrySnapshots
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.DeviceId == id, cancellationToken);

        if (snapshot is null)
        {
            return NotFound(BuildError("NOT_FOUND.TELEMETRY", "Telemetry has not been reported for this agent."));
        }

        var telemetry = ToAgentTelemetryDto(snapshot);
        if (telemetry is null)
        {
            return NotFound(BuildError("NOT_FOUND.TELEMETRY", "Telemetry payload could not be read."));
        }

        if (!PortalAccessResolver.HasSensitiveDataAccess(User))
        {
            // Non-sensitive roles see only operational-status fields.
            // All FCC infrastructure details and failure counters are redacted.
            telemetry = telemetry with
            {
                FccHealth = new FccHealthStatusDto
                {
                    IsReachable = telemetry.FccHealth.IsReachable,
                    FccVendor = telemetry.FccHealth.FccVendor,
                    FccHost = "***",
                    FccPort = 0,
                    LastHeartbeatAtUtc = null,
                    HeartbeatAgeSeconds = null,
                    ConsecutiveHeartbeatFailures = 0
                }
            };
        }

        return Ok(telemetry);
    }

    [HttpGet("{id:guid}/events")]
    [ProducesResponseType(typeof(IReadOnlyList<AgentAuditEventDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEvents(
        Guid id,
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
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
            .ForPortal(access)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (agent is null)
        {
            return NotFound(BuildError("NOT_FOUND.AGENT", "Agent was not found."));
        }

        var canViewSensitive = PortalAccessResolver.HasSensitiveDataAccess(User);

        // Single keyset-paginated query on the indexed entity_id column (M-18 fix).
        // No in-memory loop — the DB does all filtering in O(log N) via ix_audit_entity_time.
        IQueryable<AuditEvent> eventsQuery = _db.AuditEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.EntityId == id);

        if (PortalCursor.TryDecode(cursor, out var cursorTimestamp, out var cursorId))
        {
            eventsQuery = eventsQuery.Where(item =>
                item.CreatedAt < cursorTimestamp
                || (item.CreatedAt == cursorTimestamp && item.Id.CompareTo(cursorId) < 0));
        }

        var rows = await eventsQuery
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var events = rows.Select(item =>
        {
            var payload = PortalJson.ParseJson(item.Payload);
            return new AgentAuditEventDto
            {
                Id = item.Id,
                DeviceId = id,
                EventType = item.EventType,
                Description = PortalJson.TryReadString(payload, "message")
                    ?? PortalJson.TryReadString(payload, "payload", "message")
                    ?? item.EventType,
                PreviousState = PortalJson.TryReadString(payload, "previousState")
                    ?? PortalJson.TryReadString(payload, "payload", "previousState"),
                NewState = PortalJson.TryReadString(payload, "newState")
                    ?? PortalJson.TryReadString(payload, "payload", "newState"),
                OccurredAtUtc = PortalJson.ReadTimestamp(item, payload),
                Metadata = canViewSensitive ? payload : null
            };
        }).ToList();

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

    private static AgentTelemetryDto? ToAgentTelemetryDto(AgentTelemetrySnapshot snapshot)
    {
        if (TelemetrySnapshotPayload.TryDeserialize(
                snapshot.PayloadJson,
                PortalJson.SerializerOptions,
                out var compactPayload))
        {
            return new AgentTelemetryDto
            {
                SchemaVersion = compactPayload!.SchemaVersion,
                DeviceId = snapshot.DeviceId,
                SiteCode = snapshot.SiteCode,
                LegalEntityId = snapshot.LegalEntityId,
                ReportedAtUtc = snapshot.ReportedAtUtc,
                SequenceNumber = compactPayload.SequenceNumber,
                ConnectivityState = snapshot.ConnectivityState.ToString(),
                Device = new DeviceStatusDto
                {
                    BatteryPercent = snapshot.BatteryPercent,
                    IsCharging = snapshot.IsCharging,
                    StorageFreeMb = compactPayload.Device.StorageFreeMb,
                    StorageTotalMb = compactPayload.Device.StorageTotalMb,
                    MemoryFreeMb = compactPayload.Device.MemoryFreeMb,
                    MemoryTotalMb = compactPayload.Device.MemoryTotalMb,
                    AppVersion = compactPayload.Device.AppVersion,
                    AppUptimeSeconds = compactPayload.Device.AppUptimeSeconds,
                    OsVersion = compactPayload.Device.OsVersion,
                    DeviceModel = compactPayload.Device.DeviceModel
                },
                FccHealth = new FccHealthStatusDto
                {
                    IsReachable = compactPayload.FccHealth.IsReachable,
                    LastHeartbeatAtUtc = snapshot.LastHeartbeatAtUtc,
                    HeartbeatAgeSeconds = snapshot.HeartbeatAgeSeconds,
                    FccVendor = snapshot.FccVendor.ToString(),
                    FccHost = snapshot.FccHost,
                    FccPort = snapshot.FccPort,
                    ConsecutiveHeartbeatFailures = snapshot.ConsecutiveHeartbeatFailures
                },
                Buffer = new BufferStatusDto
                {
                    TotalRecords = compactPayload.Buffer.TotalRecords,
                    PendingUploadCount = snapshot.PendingUploadCount,
                    SyncedCount = compactPayload.Buffer.SyncedCount,
                    SyncedToOdooCount = compactPayload.Buffer.SyncedToOdooCount,
                    FailedCount = compactPayload.Buffer.FailedCount,
                    OldestPendingAtUtc = compactPayload.Buffer.OldestPendingAtUtc,
                    BufferSizeMb = compactPayload.Buffer.BufferSizeMb
                },
                Sync = new SyncStatusDto
                {
                    LastSyncAttemptUtc = compactPayload.Sync.LastSyncAttemptUtc,
                    LastSuccessfulSyncUtc = compactPayload.Sync.LastSuccessfulSyncUtc,
                    SyncLagSeconds = snapshot.SyncLagSeconds,
                    LastStatusPollUtc = compactPayload.Sync.LastStatusPollUtc,
                    LastConfigPullUtc = compactPayload.Sync.LastConfigPullUtc,
                    ConfigVersion = compactPayload.Sync.ConfigVersion,
                    UploadBatchSize = compactPayload.Sync.UploadBatchSize
                },
                ErrorCounts = new ErrorCountsDto
                {
                    FccConnectionErrors = compactPayload.ErrorCounts.FccConnectionErrors,
                    CloudUploadErrors = compactPayload.ErrorCounts.CloudUploadErrors,
                    CloudAuthErrors = compactPayload.ErrorCounts.CloudAuthErrors,
                    LocalApiErrors = compactPayload.ErrorCounts.LocalApiErrors,
                    BufferWriteErrors = compactPayload.ErrorCounts.BufferWriteErrors,
                    AdapterNormalizationErrors = compactPayload.ErrorCounts.AdapterNormalizationErrors,
                    PreAuthErrors = compactPayload.ErrorCounts.PreAuthErrors
                }
            };
        }

        return JsonSerializer.Deserialize<AgentTelemetryDto>(snapshot.PayloadJson, PortalJson.SerializerOptions);
    }

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

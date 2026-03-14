using System.Text.Json;
using FccMiddleware.Api.AgentControl;
using FccMiddleware.Api.Portal;
using FccMiddleware.Contracts.AgentControl;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Models;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/agents")]
[Authorize(Policy = "PortalUser")]
public sealed class AgentsController : PortalControllerBase
{
    private readonly FccMiddlewareDbContext _db;
    private readonly PortalAccessResolver _accessResolver;
    private readonly IOptions<AgentCommandsOptions> _agentCommandsOptions;

    public AgentsController(
        FccMiddlewareDbContext db,
        PortalAccessResolver accessResolver,
        IOptions<AgentCommandsOptions> agentCommandsOptions)
    {
        _db = db;
        _accessResolver = accessResolver;
        _agentCommandsOptions = agentCommandsOptions;
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

        if (!TryParseAgentStatus(status, out var statusFilter))
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

        if (statusFilter.HasValue)
        {
            baseQuery = baseQuery.Where(agent => agent.Status == statusFilter.Value);
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

        var siteIds = page.Select(agent => agent.SiteId).Distinct().ToArray();
        var sitePeers = await _db.AgentRegistrations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(agent =>
                agent.LegalEntityId == legalEntityId
                && siteIds.Contains(agent.SiteId)
                && agent.IsActive
                && agent.Status == AgentRegistrationStatus.ACTIVE)
            .OrderBy(agent => agent.SiteHaPriority)
            .ThenBy(agent => agent.RegisteredAt)
            .ToListAsync(cancellationToken);

        var leaderBySite = sitePeers
            .GroupBy(agent => agent.SiteId)
            .ToDictionary(group => group.Key, group => DetermineLeader(group));
        var epochBySite = sitePeers
            .GroupBy(agent => agent.SiteId)
            .ToDictionary(
                group => group.Key,
                group => group.Any() ? Math.Max(1, group.Max(agent => agent.LeaderEpochSeen ?? 0)) : 0L);

        var data = page
            .Select(agent =>
            {
                snapshots.TryGetValue(agent.Id, out var snapshot);
                leaderBySite.TryGetValue(agent.SiteId, out var leader);
                epochBySite.TryGetValue(agent.SiteId, out var leaderEpoch);
                var currentRole = ResolveCurrentRole(agent, leader);
                return new AgentHealthSummaryDto
                {
                    DeviceId = agent.Id,
                    SiteCode = agent.SiteCode,
                    SiteName = agent.Site.SiteName,
                    LegalEntityId = agent.LegalEntityId,
                    DeviceClass = agent.DeviceClass,
                    AgentVersion = agent.AgentVersion,
                    RoleCapability = agent.RoleCapability,
                    Priority = agent.SiteHaPriority,
                    CurrentRole = currentRole,
                    IsCurrentLeader = leader?.Id == agent.Id,
                    LeaderEpoch = leaderEpoch,
                    Capabilities = DeserializeCapabilities(agent.CapabilitiesJson),
                    PeerApiBaseUrl = agent.PeerApiBaseUrl,
                    Status = agent.Status.ToString(),
                    HasTelemetry = snapshot is not null,
                    ConnectivityState = snapshot?.ConnectivityState.ToString(),
                    BatteryPercent = snapshot?.BatteryPercent,
                    IsCharging = snapshot?.IsCharging,
                    BufferDepth = snapshot?.PendingUploadCount,
                    SyncLagSeconds = snapshot?.SyncLagSeconds,
                    LastReplicationLagSeconds = agent.LastReplicationLagSeconds,
                    LastTelemetryAt = snapshot?.ReportedAtUtc,
                    LastSeenAt = agent.LastSeenAt,
                    SuspensionReasonCode = agent.SuspensionReasonCode,
                    ApprovalGrantedAt = agent.ApprovalGrantedAt
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

    [HttpGet("{id:guid}/commands")]
    [ProducesResponseType(typeof(PortalPagedResult<CreateAgentCommandResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCommands(
        Guid id,
        [FromQuery] string? cursor = null,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!_agentCommandsOptions.Value.Enabled)
        {
            return NotFound(BuildError("FEATURE_DISABLED", "Agent command APIs are disabled."));
        }

        if (pageSize is < 1 or > 100)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_PAGE_SIZE", "pageSize must be between 1 and 100."));
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

        await ExpirePendingCommandsAsync(id, cancellationToken);

        IQueryable<AgentCommand> query = _db.AgentCommands
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.DeviceId == id);

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

        return Ok(new PortalPagedResult<CreateAgentCommandResponse>
        {
            Data = rows.Select(ToCreateAgentCommandResponse).ToList(),
            Meta = new PortalPageMeta
            {
                PageSize = rows.Count,
                HasMore = hasMore,
                NextCursor = nextCursor,
                TotalCount = totalCount
            }
        });
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
            DeviceClass = agent.DeviceClass,
            DeviceSerialNumber = agent.DeviceSerialNumber,
            DeviceModel = agent.DeviceModel,
            OsVersion = agent.OsVersion,
            AgentVersion = agent.AgentVersion,
            RoleCapability = agent.RoleCapability,
            Priority = agent.SiteHaPriority,
            CurrentRole = agent.CurrentRole,
            Capabilities = DeserializeCapabilities(agent.CapabilitiesJson),
            PeerApiBaseUrl = agent.PeerApiBaseUrl,
            PeerApiAdvertisedHost = agent.PeerApiAdvertisedHost,
            PeerApiPort = agent.PeerApiPort,
            PeerApiTlsEnabled = agent.PeerApiTlsEnabled,
            LeaderEpochSeen = agent.LeaderEpochSeen,
            LastReplicationLagSeconds = agent.LastReplicationLagSeconds,
            Status = agent.Status.ToString(),
            RegisteredAt = agent.RegisteredAt,
            LastSeenAt = agent.LastSeenAt,
            SuspensionReasonCode = agent.SuspensionReasonCode,
            SuspensionReason = agent.SuspensionReason,
            ReplacementForDeviceId = agent.ReplacementForDeviceId,
            ApprovalGrantedAt = agent.ApprovalGrantedAt,
            ApprovalGrantedByActorDisplay = agent.ApprovalGrantedByActorDisplay
        };

    private static AgentRegistration? DetermineLeader(IEnumerable<AgentRegistration> agents) =>
        agents
            .Where(agent => !string.Equals(agent.RoleCapability, "READ_ONLY", StringComparison.OrdinalIgnoreCase))
            .OrderBy(agent => agent.SiteHaPriority)
            .ThenBy(agent => string.Equals(agent.DeviceClass, "DESKTOP", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(agent => agent.RegisteredAt)
            .FirstOrDefault();

    private static string ResolveCurrentRole(AgentRegistration agent, AgentRegistration? leader)
    {
        if (!string.IsNullOrWhiteSpace(agent.CurrentRole))
        {
            return agent.CurrentRole!;
        }

        if (!agent.IsActive || agent.Status != AgentRegistrationStatus.ACTIVE)
        {
            return "OFFLINE";
        }

        if (string.Equals(agent.RoleCapability, "READ_ONLY", StringComparison.OrdinalIgnoreCase))
        {
            return "READ_ONLY";
        }

        return leader?.Id == agent.Id ? "PRIMARY" : "STANDBY_HOT";
    }

    private static string[] DeserializeCapabilities(string? capabilitiesJson)
    {
        if (string.IsNullOrWhiteSpace(capabilitiesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(capabilitiesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static CreateAgentCommandResponse ToCreateAgentCommandResponse(AgentCommand command) =>
        new()
        {
            CommandId = command.Id,
            DeviceId = command.DeviceId,
            LegalEntityId = command.LegalEntityId,
            SiteCode = command.SiteCode,
            CommandType = command.CommandType,
            Status = command.Status,
            Reason = command.Reason,
            Payload = ParseJsonOrNull(command.PayloadJson),
            CreatedAt = command.CreatedAt,
            ExpiresAt = command.ExpiresAt,
            CreatedByActorId = command.CreatedByActorId,
            CreatedByActorDisplay = command.CreatedByActorDisplay
        };

    private static JsonElement? ParseJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task ExpirePendingCommandsAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var commands = await _db.AgentCommands
            .IgnoreQueryFilters()
            .Where(item =>
                item.DeviceId == deviceId
                && item.ExpiresAt <= now
                && (item.Status == AgentCommandStatus.PENDING || item.Status == AgentCommandStatus.DELIVERY_HINT_SENT))
            .ToListAsync(cancellationToken);

        if (commands.Count == 0)
        {
            return;
        }

        foreach (var command in commands)
        {
            command.Status = AgentCommandStatus.EXPIRED;
            command.LastError = "Command expired before acknowledgement.";
            command.UpdatedAt = now;

            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                LegalEntityId = command.LegalEntityId,
                EventType = AgentControlAuditEventTypes.AgentCommandExpired,
                CorrelationId = Guid.NewGuid(),
                SiteCode = command.SiteCode,
                Source = nameof(AgentsController),
                EntityId = command.DeviceId,
                Payload = JsonSerializer.Serialize(new
                {
                    CommandId = command.Id,
                    DeviceId = command.DeviceId,
                    command.CommandType,
                    ExpiredAt = now
                })
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

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

    private static bool TryParseAgentStatus(string? status, out AgentRegistrationStatus? parsedStatus)
    {
        parsedStatus = null;
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        if (Enum.TryParse<AgentRegistrationStatus>(status, true, out var statusValue))
        {
            parsedStatus = statusValue;
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

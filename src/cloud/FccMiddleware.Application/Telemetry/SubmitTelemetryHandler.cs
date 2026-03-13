using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Events;
using FccMiddleware.Domain.Models;
using MediatR;

namespace FccMiddleware.Application.Telemetry;

public sealed class SubmitTelemetryHandler : IRequestHandler<SubmitTelemetryCommand, Result<SubmitTelemetryResult>>
{
    private static readonly TimeSpan TelemetryAuditInterval = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ITelemetryDbContext _db;

    public SubmitTelemetryHandler(ITelemetryDbContext db)
    {
        _db = db;
    }

    public async Task<Result<SubmitTelemetryResult>> Handle(
        SubmitTelemetryCommand request,
        CancellationToken cancellationToken)
    {
        var agent = await _db.FindAgentByDeviceIdAsync(request.DeviceId, cancellationToken);
        if (agent is null || !agent.IsActive)
        {
            return Result<SubmitTelemetryResult>.Failure("DEVICE_NOT_FOUND",
                "No active agent registration found for this device.");
        }

        if (!string.Equals(agent.SiteCode, request.SiteCode, StringComparison.Ordinal))
        {
            return Result<SubmitTelemetryResult>.Failure("SITE_MISMATCH",
                "Device is not registered at the claimed site.");
        }

        if (agent.LegalEntityId != request.LegalEntityId)
        {
            return Result<SubmitTelemetryResult>.Failure("SITE_MISMATCH",
                "Device is not registered under the claimed legal entity.");
        }

        var snapshot = await _db.FindTelemetrySnapshotByDeviceIdAsync(request.DeviceId, cancellationToken);
        if (snapshot is not null
            && TelemetrySnapshotPayload.TryReadSequenceNumber(snapshot.PayloadJson, JsonOptions, out var persistedSequenceNumber)
            && request.Payload.SequenceNumber <= persistedSequenceNumber)
        {
            return Result<SubmitTelemetryResult>.Success(new SubmitTelemetryResult
            {
                CorrelationId = CreateDeterministicCorrelationId(request.DeviceId, request.Payload.SequenceNumber),
                IsDuplicate = true
            });
        }

        var correlationId = CreateDeterministicCorrelationId(request.DeviceId, request.Payload.SequenceNumber);
        if (await _db.HasAuditEventAsync(correlationId, "AgentHealthReported", cancellationToken))
        {
            return Result<SubmitTelemetryResult>.Success(new SubmitTelemetryResult
            {
                CorrelationId = correlationId,
                IsDuplicate = true
            });
        }

        var now = DateTimeOffset.UtcNow;
        agent.LastSeenAt = now;
        agent.UpdatedAt = now;

        var domainEvent = new AgentHealthReported
        {
            DeviceId = request.DeviceId.ToString(),
            BufferDepth = request.Payload.Buffer.PendingUploadCount,
            LastFccHeartbeat = request.Payload.FccHealth.LastHeartbeatAtUtc,
            SyncLagSeconds = request.Payload.Sync.SyncLagSeconds ?? 0,
            BatteryPercent = request.Payload.Device.BatteryPercent
        };

        var payloadJson = JsonSerializer.Serialize(
            TelemetrySnapshotPayload.FromTelemetry(request.Payload),
            JsonOptions);
        var isNewSnapshot = snapshot is null;
        if (isNewSnapshot)
        {
            snapshot = new AgentTelemetrySnapshot
            {
                DeviceId = request.DeviceId,
                CreatedAt = now
            };
            _db.AddTelemetrySnapshot(snapshot);
        }

        var latestHealthAuditAt = await _db.GetLatestAuditEventCreatedAtAsync(
            request.DeviceId,
            domainEvent.EventType,
            cancellationToken);
        var shouldEmitHealthAudit = isNewSnapshot
            || latestHealthAuditAt is null
            || now - latestHealthAuditAt.Value >= TelemetryAuditInterval;

        if (shouldEmitHealthAudit)
        {
            var envelope = new
            {
                eventId = Guid.NewGuid(),
                eventType = domainEvent.EventType,
                occurredAt = now,
                correlationId,
                legalEntityId = request.LegalEntityId,
                siteCode = request.SiteCode,
                source = "edge-agent",
                data = new
                {
                    sequenceNumber = request.Payload.SequenceNumber,
                    reportedAtUtc = request.Payload.ReportedAtUtc,
                    connectivityState = request.Payload.ConnectivityState,
                    summary = domainEvent
                }
            };

            _db.AddAuditEvent(new AuditEvent
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                LegalEntityId = request.LegalEntityId,
                EventType = domainEvent.EventType,
                EntityId = request.DeviceId,
                CorrelationId = correlationId,
                SiteCode = request.SiteCode,
                Source = "edge-agent",
                Payload = JsonSerializer.Serialize(envelope, JsonOptions)
            });
        }

        // Detect connectivity state change and emit audit event (skip for first telemetry)
        var previousConnectivity = snapshot.ConnectivityState;
        var newConnectivity = request.Payload.ConnectivityState;
        if (!isNewSnapshot && previousConnectivity != newConnectivity)
        {
            _db.AddAuditEvent(new AuditEvent
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                LegalEntityId = request.LegalEntityId,
                EventType = "CONNECTIVITY_STATE_CHANGED",
                EntityId = request.DeviceId,
                CorrelationId = Guid.NewGuid(),
                SiteCode = request.SiteCode,
                Source = "SubmitTelemetryHandler",
                Payload = JsonSerializer.Serialize(new
                {
                    deviceId = request.DeviceId,
                    previousState = previousConnectivity.ToString(),
                    newState = newConnectivity.ToString(),
                    message = $"Connectivity changed from {previousConnectivity} to {newConnectivity}",
                    occurredAt = now,
                }, JsonOptions)
            });
        }

        snapshot.LegalEntityId = request.LegalEntityId;
        snapshot.SiteCode = request.SiteCode;
        snapshot.ReportedAtUtc = request.Payload.ReportedAtUtc;
        snapshot.ConnectivityState = request.Payload.ConnectivityState;
        snapshot.PayloadJson = payloadJson;
        snapshot.BatteryPercent = request.Payload.Device.BatteryPercent;
        snapshot.IsCharging = request.Payload.Device.IsCharging;
        snapshot.PendingUploadCount = request.Payload.Buffer.PendingUploadCount;
        snapshot.SyncLagSeconds = request.Payload.Sync.SyncLagSeconds;
        snapshot.LastHeartbeatAtUtc = request.Payload.FccHealth.LastHeartbeatAtUtc;
        snapshot.HeartbeatAgeSeconds = request.Payload.FccHealth.HeartbeatAgeSeconds;
        snapshot.FccVendor = request.Payload.FccHealth.FccVendor;
        snapshot.FccHost = request.Payload.FccHealth.FccHost;
        snapshot.FccPort = request.Payload.FccHealth.FccPort;
        snapshot.ConsecutiveHeartbeatFailures = request.Payload.FccHealth.ConsecutiveHeartbeatFailures;
        snapshot.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken);

        return Result<SubmitTelemetryResult>.Success(new SubmitTelemetryResult
        {
            CorrelationId = correlationId,
            IsDuplicate = false
        });
    }

    private static Guid CreateDeterministicCorrelationId(Guid deviceId, int sequenceNumber)
    {
        Span<byte> sequenceBytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(sequenceBytes, sequenceNumber);

        var payload = $"{deviceId:N}:{Convert.ToHexString(sequenceBytes)}";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}

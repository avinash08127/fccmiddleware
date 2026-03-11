using System.Text;
using System.Text.Json;
using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Events;
using MediatR;

namespace FccMiddleware.Application.Telemetry;

public sealed class SubmitTelemetryHandler : IRequestHandler<SubmitTelemetryCommand, Result<SubmitTelemetryResult>>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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
                telemetry = request.Payload,
                summary = domainEvent
            }
        };

        _db.AddAuditEvent(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = request.LegalEntityId,
            EventType = domainEvent.EventType,
            CorrelationId = correlationId,
            SiteCode = request.SiteCode,
            Source = "edge-agent",
            Payload = JsonSerializer.Serialize(envelope, JsonOptions)
        });

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

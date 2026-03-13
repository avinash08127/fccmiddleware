using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Application.Telemetry;

public interface ITelemetryDbContext
{
    Task<AgentRegistration?> FindAgentByDeviceIdAsync(Guid deviceId, CancellationToken ct);
    Task<bool> HasAuditEventAsync(Guid correlationId, string eventType, CancellationToken ct);
    Task<DateTimeOffset?> GetLatestAuditEventCreatedAtAsync(Guid deviceId, string eventType, CancellationToken ct);
    Task<AgentTelemetrySnapshot?> FindTelemetrySnapshotByDeviceIdAsync(Guid deviceId, CancellationToken ct);
    void AddAuditEvent(AuditEvent auditEvent);
    void AddTelemetrySnapshot(AgentTelemetrySnapshot snapshot);
    Task<int> SaveChangesAsync(CancellationToken ct);
}

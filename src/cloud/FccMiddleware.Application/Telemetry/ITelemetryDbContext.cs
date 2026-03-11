using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Application.Telemetry;

public interface ITelemetryDbContext
{
    Task<AgentRegistration?> FindAgentByDeviceIdAsync(Guid deviceId, CancellationToken ct);
    Task<bool> HasAuditEventAsync(Guid correlationId, string eventType, CancellationToken ct);
    void AddAuditEvent(AuditEvent auditEvent);
    Task<int> SaveChangesAsync(CancellationToken ct);
}

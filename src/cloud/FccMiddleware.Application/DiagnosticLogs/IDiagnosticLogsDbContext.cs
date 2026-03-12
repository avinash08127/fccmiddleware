using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Application.DiagnosticLogs;

public interface IDiagnosticLogsDbContext
{
    Task<FccMiddleware.Domain.Entities.AgentRegistration?> FindAgentByDeviceIdAsync(Guid deviceId);
    void AddDiagnosticLog(AgentDiagnosticLog log);
    Task<List<AgentDiagnosticLog>> GetRecentDiagnosticLogsAsync(Guid deviceId, int maxBatches);
    Task<int> SaveChangesAsync(CancellationToken ct);
}

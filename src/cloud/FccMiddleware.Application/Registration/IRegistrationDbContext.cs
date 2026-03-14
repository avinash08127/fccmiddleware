using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Application.Registration;

/// <summary>
/// Data access abstraction for device registration operations.
/// Implemented by FccMiddlewareDbContext — keeps EF Core dependency out of Application layer.
/// </summary>
public interface IRegistrationDbContext
{
    Task<BootstrapToken?> FindBootstrapTokenByHashAsync(string tokenHash, CancellationToken ct);
    Task<BootstrapToken?> FindBootstrapTokenByIdAsync(Guid tokenId, CancellationToken ct);
    Task<int> CountActiveBootstrapTokensForSiteAsync(string siteCode, Guid legalEntityId, CancellationToken ct);
    Task<Site?> FindSiteBySiteCodeAsync(string siteCode, CancellationToken ct);
    Task<List<AgentRegistration>> FindActiveAgentsForSiteAsync(Guid siteId, CancellationToken ct);
    Task<AgentRegistration?> FindAgentByIdAsync(Guid deviceId, CancellationToken ct);
    Task<AgentRegistration?> FindSuspendedAgentForSiteAndSerialAsync(Guid siteId, string deviceSerialNumber, CancellationToken ct);
    Task<DeviceRefreshToken?> FindRefreshTokenByHashAsync(string tokenHash, CancellationToken ct);
    Task<List<DeviceRefreshToken>> GetActiveRefreshTokensForDeviceAsync(Guid deviceId, CancellationToken ct);

    void AddAgentRegistration(AgentRegistration registration);
    void AddBootstrapToken(BootstrapToken token);
    void AddDeviceRefreshToken(DeviceRefreshToken token);
    void AddAuditEvent(AuditEvent auditEvent);

    Task<int> SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Saves all pending changes. Returns false if a concurrency conflict was detected
    /// (e.g., bootstrap token modified by another concurrent request), true on success.
    /// </summary>
    Task<bool> TrySaveChangesAsync(CancellationToken ct);
}

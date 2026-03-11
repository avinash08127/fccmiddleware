using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Application.Registration;

/// <summary>
/// Data access abstraction for device registration operations.
/// Implemented by FccMiddlewareDbContext — keeps EF Core dependency out of Application layer.
/// </summary>
public interface IRegistrationDbContext
{
    Task<BootstrapToken?> FindBootstrapTokenByHashAsync(string tokenHash, CancellationToken ct);
    Task<Site?> FindSiteBySiteCodeAsync(string siteCode, CancellationToken ct);
    Task<AgentRegistration?> FindActiveAgentForSiteAsync(Guid siteId, CancellationToken ct);
    Task<AgentRegistration?> FindAgentByIdAsync(Guid deviceId, CancellationToken ct);
    Task<DeviceRefreshToken?> FindRefreshTokenByHashAsync(string tokenHash, CancellationToken ct);
    Task<List<DeviceRefreshToken>> GetActiveRefreshTokensForDeviceAsync(Guid deviceId, CancellationToken ct);

    void AddAgentRegistration(AgentRegistration registration);
    void AddBootstrapToken(BootstrapToken token);
    void AddDeviceRefreshToken(DeviceRefreshToken token);

    Task<int> SaveChangesAsync(CancellationToken ct);
}

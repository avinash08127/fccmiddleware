using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Application.AgentConfig;

/// <summary>
/// Data access abstraction for Edge Agent config distribution.
/// Implemented by FccMiddlewareDbContext — keeps EF Core dependency out of Application layer.
/// </summary>
public interface IAgentConfigDbContext
{
    /// <summary>
    /// Loads FccConfig (with Site, LegalEntity, Pumps, Nozzles, Products) for the given site.
    /// Returns null if no active FccConfig exists for the site.
    /// </summary>
    Task<FccConfig?> GetFccConfigWithSiteDataAsync(string siteCode, Guid legalEntityId, CancellationToken ct);

    Task<AdapterDefaultConfig?> GetAdapterDefaultConfigAsync(Guid legalEntityId, string adapterKey, CancellationToken ct);

    Task<SiteAdapterOverride?> GetSiteAdapterOverrideAsync(Guid siteId, string adapterKey, CancellationToken ct);

    /// <summary>
    /// Finds the agent registration for the given device ID.
    /// </summary>
    Task<AgentRegistration?> FindAgentByDeviceIdAsync(Guid deviceId, CancellationToken ct);
}

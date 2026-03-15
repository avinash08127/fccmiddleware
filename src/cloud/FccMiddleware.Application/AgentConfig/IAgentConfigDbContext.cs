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

    Task<List<AgentRegistration>> GetSiteAgentsAsync(Guid siteId, CancellationToken ct);

    /// <summary>
    /// Finds the agent registration for the given device ID.
    /// </summary>
    Task<AgentRegistration?> FindAgentByDeviceIdAsync(Guid deviceId, CancellationToken ct);

    /// <summary>
    /// P2-15: Loads the site entity by ID for HA leader epoch validation.
    /// </summary>
    Task<Site?> GetSiteByIdAsync(Guid siteId, CancellationToken ct);

    /// <summary>
    /// P2-15: Records the agent-elected leader and epoch on the site.
    /// Called when the cloud accepts a write with a higher epoch than previously recorded.
    /// </summary>
    Task UpdateSiteHaLeaderAsync(Guid siteId, Guid leaderAgentId, long epoch, CancellationToken ct);
}

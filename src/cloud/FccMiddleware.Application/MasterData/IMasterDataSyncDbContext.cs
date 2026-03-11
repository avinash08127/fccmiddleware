using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Application.MasterData;

/// <summary>
/// DB operations required by the master data sync handlers.
/// All read methods bypass global query filters (IgnoreQueryFilters) so that
/// inactive/cross-tenant records are visible to the sync logic.
/// </summary>
public interface IMasterDataSyncDbContext
{
    // ─── Legal entities ────────────────────────────────────────────────────
    Task<List<LegalEntity>> GetLegalEntitiesByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
    Task<List<Guid>> GetActiveLegalEntityIdsAsync(CancellationToken ct);

    // ─── Sites ────────────────────────────────────────────────────────────
    Task<List<Site>> GetSitesByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
    Task<List<Guid>> GetActiveSiteIdsAsync(CancellationToken ct);

    // ─── Pumps ────────────────────────────────────────────────────────────
    Task<List<Pump>> GetPumpsByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
    Task<List<Guid>> GetActivePumpIdsAsync(CancellationToken ct);

    /// <summary>Returns sites keyed by site code. Used by pump sync to resolve SiteId/LegalEntityId.</summary>
    Task<Dictionary<string, Site>> GetSitesByCodesAsync(IReadOnlyList<string> siteCodes, CancellationToken ct);

    // ─── Nozzles ──────────────────────────────────────────────────────────
    Task<List<Nozzle>> GetNozzlesByPumpIdsAsync(IReadOnlyList<Guid> pumpIds, CancellationToken ct);

    // ─── Products ────────────────────────────────────────────────────────
    Task<List<Product>> GetProductsByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
    Task<List<Guid>> GetActiveProductIdsAsync(Guid legalEntityId, CancellationToken ct);
    Task<Product?> FindProductByCodeAsync(Guid legalEntityId, string productCode, CancellationToken ct);

    // ─── Operators ────────────────────────────────────────────────────────
    Task<List<Operator>> GetOperatorsByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
    Task<List<Guid>> GetActiveOperatorIdsAsync(Guid legalEntityId, CancellationToken ct);

    // ─── Mutations ────────────────────────────────────────────────────────
    void AddLegalEntity(LegalEntity entity);
    void AddSite(Site entity);
    void AddPump(Pump entity);
    void AddNozzle(Nozzle entity);
    void AddProduct(Product entity);
    void AddOperator(Operator entity);
    void AddOutboxMessage(OutboxMessage message);

    Task<int> SaveChangesAsync(CancellationToken ct);
}

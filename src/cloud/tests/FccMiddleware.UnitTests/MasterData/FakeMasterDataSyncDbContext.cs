using FccMiddleware.Application.MasterData;
using FccMiddleware.Domain.Entities;

namespace FccMiddleware.UnitTests.MasterData;

internal sealed class FakeMasterDataSyncDbContext : IMasterDataSyncDbContext
{
    public List<LegalEntity> LegalEntities { get; } = [];
    public List<Site> Sites { get; } = [];
    public List<Pump> Pumps { get; } = [];
    public List<Nozzle> Nozzles { get; } = [];
    public List<Product> Products { get; } = [];
    public List<Operator> Operators { get; } = [];
    public List<OutboxMessage> OutboxMessages { get; } = [];

    public Task<List<LegalEntity>> GetLegalEntitiesByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        Task.FromResult(LegalEntities.Where(entity => ids.Contains(entity.Id)).ToList());

    public Task<List<Guid>> GetActiveLegalEntityIdsAsync(CancellationToken ct) =>
        Task.FromResult(LegalEntities.Where(entity => entity.IsActive).Select(entity => entity.Id).ToList());

    public Task<List<Site>> GetSitesByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        Task.FromResult(Sites.Where(site => ids.Contains(site.Id)).ToList());

    public Task<List<Guid>> GetActiveSiteIdsAsync(CancellationToken ct) =>
        Task.FromResult(Sites.Where(site => site.IsActive).Select(site => site.Id).ToList());

    public Task<List<Pump>> GetPumpsByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        Task.FromResult(Pumps.Where(pump => ids.Contains(pump.Id)).ToList());

    public Task<List<Guid>> GetActivePumpIdsAsync(CancellationToken ct) =>
        Task.FromResult(Pumps.Where(pump => pump.IsActive).Select(pump => pump.Id).ToList());

    public Task<Dictionary<string, Site>> GetSitesByCodesAsync(IReadOnlyList<string> siteCodes, CancellationToken ct) =>
        Task.FromResult(Sites.Where(site => siteCodes.Contains(site.SiteCode)).ToDictionary(site => site.SiteCode));

    public Task<List<Nozzle>> GetNozzlesByPumpIdsAsync(IReadOnlyList<Guid> pumpIds, CancellationToken ct) =>
        Task.FromResult(Nozzles.Where(nozzle => pumpIds.Contains(nozzle.PumpId)).ToList());

    public Task<List<Product>> GetProductsByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        Task.FromResult(Products.Where(product => ids.Contains(product.Id)).ToList());

    public Task<List<Guid>> GetActiveProductIdsAsync(Guid legalEntityId, CancellationToken ct) =>
        Task.FromResult(Products
            .Where(product => product.LegalEntityId == legalEntityId && product.IsActive)
            .Select(product => product.Id)
            .ToList());

    public Task<Product?> FindProductByCodeAsync(Guid legalEntityId, string productCode, CancellationToken ct) =>
        Task.FromResult(Products.FirstOrDefault(product =>
            product.LegalEntityId == legalEntityId && product.ProductCode == productCode));

    public Task<List<Operator>> GetOperatorsByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        Task.FromResult(Operators.Where(item => ids.Contains(item.Id)).ToList());

    public Task<List<Guid>> GetActiveOperatorIdsAsync(Guid legalEntityId, CancellationToken ct) =>
        Task.FromResult(Operators
            .Where(item => item.LegalEntityId == legalEntityId && item.IsActive)
            .Select(item => item.Id)
            .ToList());

    public void AddLegalEntity(LegalEntity entity) => LegalEntities.Add(entity);

    public void AddSite(Site entity) => Sites.Add(entity);

    public void AddPump(Pump entity) => Pumps.Add(entity);

    public void AddNozzle(Nozzle entity) => Nozzles.Add(entity);

    public void AddProduct(Product entity) => Products.Add(entity);

    public void AddOperator(Operator entity) => Operators.Add(entity);

    public void AddOutboxMessage(OutboxMessage message) => OutboxMessages.Add(message);

    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
}

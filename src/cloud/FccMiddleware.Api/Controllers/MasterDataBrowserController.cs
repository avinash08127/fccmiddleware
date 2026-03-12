using FccMiddleware.Api.Portal;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/master-data")]
[Authorize(Policy = "PortalUser")]
public sealed class MasterDataBrowserController : PortalControllerBase
{
    private readonly FccMiddlewareDbContext _db;
    private readonly PortalAccessResolver _accessResolver;

    public MasterDataBrowserController(FccMiddlewareDbContext db, PortalAccessResolver accessResolver)
    {
        _db = db;
        _accessResolver = accessResolver;
    }

    [HttpGet("legal-entities")]
    [ProducesResponseType(typeof(IReadOnlyList<PortalLegalEntityDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLegalEntities(CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var query = _db.LegalEntities
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AsQueryable();

        if (!access.AllowAllLegalEntities)
        {
            query = query.Where(item => access.ScopedLegalEntityIds.Contains(item.Id));
        }

        var entities = await query
            .OrderBy(item => item.Name)
            .Select(item => new PortalLegalEntityDto
            {
                Id = item.Id,
                Code = item.CountryCode,
                Name = item.Name,
                CurrencyCode = item.CurrencyCode,
                Country = item.CountryCode,
                IsActive = item.IsActive,
                UpdatedAt = item.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(entities);
    }

    [HttpGet("products")]
    [ProducesResponseType(typeof(IReadOnlyList<ProductDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProducts([FromQuery] Guid legalEntityId, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        if (!access.CanAccess(legalEntityId))
        {
            return Forbid();
        }

        var products = await _db.Products
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.LegalEntityId == legalEntityId)
            .OrderBy(item => item.ProductCode)
            .Select(item => new ProductDto
            {
                Id = item.Id,
                CanonicalCode = item.ProductCode,
                DisplayName = item.ProductName,
                IsActive = item.IsActive,
                UpdatedAt = item.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(products);
    }

    [HttpGet("sync-status")]
    [ProducesResponseType(typeof(IReadOnlyList<MasterDataSyncStatusDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSyncStatus(CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var legalEntityIds = access.AllowAllLegalEntities
            ? null
            : access.ScopedLegalEntityIds.ToHashSet();

        async Task<MasterDataSyncStatusDto> BuildAsync<T>(
            string entityType,
            IQueryable<T> query,
            Func<T, DateTimeOffset> syncedAt,
            Func<T, bool> isActive,
            int staleThresholdHours)
            where T : class
        {
            var items = await query.AsNoTracking().ToListAsync(cancellationToken);
            var lastSync = items.Count == 0 ? (DateTimeOffset?)null : items.Max(syncedAt);
            var activeCount = items.Count(isActive);
            var deactivatedCount = items.Count - activeCount;

            return new MasterDataSyncStatusDto
            {
                EntityType = entityType,
                LastSyncAtUtc = lastSync,
                UpsertedCount = activeCount,
                DeactivatedCount = deactivatedCount,
                ErrorCount = 0,
                IsStale = !lastSync.HasValue || lastSync.Value < DateTimeOffset.UtcNow.AddHours(-staleThresholdHours),
                StaleThresholdHours = staleThresholdHours
            };
        }

        IQueryable<FccMiddleware.Domain.Entities.Site> FilterSites(IQueryable<FccMiddleware.Domain.Entities.Site> queryable) =>
            legalEntityIds is null ? queryable : queryable.Where(item => legalEntityIds.Contains(item.LegalEntityId));

        IQueryable<FccMiddleware.Domain.Entities.Product> FilterProducts(IQueryable<FccMiddleware.Domain.Entities.Product> queryable) =>
            legalEntityIds is null ? queryable : queryable.Where(item => legalEntityIds.Contains(item.LegalEntityId));

        IQueryable<FccMiddleware.Domain.Entities.Operator> FilterOperators(IQueryable<FccMiddleware.Domain.Entities.Operator> queryable) =>
            legalEntityIds is null ? queryable : queryable.Where(item => legalEntityIds.Contains(item.LegalEntityId));

        var statuses = new List<MasterDataSyncStatusDto>
        {
            await BuildAsync(
                "legal_entities",
                _db.LegalEntities.IgnoreQueryFilters().Where(item => legalEntityIds == null || legalEntityIds.Contains(item.Id)),
                item => item.SyncedAt,
                item => item.IsActive,
                24),
            await BuildAsync(
                "sites",
                FilterSites(_db.Sites.IgnoreQueryFilters()),
                item => item.SyncedAt,
                item => item.IsActive,
                24),
            await BuildAsync(
                "pumps",
                legalEntityIds is null
                    ? _db.Pumps.IgnoreQueryFilters()
                    : _db.Pumps.IgnoreQueryFilters().Where(item => legalEntityIds.Contains(item.LegalEntityId)),
                item => item.SyncedAt,
                item => item.IsActive,
                24),
            await BuildAsync(
                "products",
                FilterProducts(_db.Products.IgnoreQueryFilters()),
                item => item.SyncedAt,
                item => item.IsActive,
                24),
            await BuildAsync(
                "operators",
                FilterOperators(_db.Operators.IgnoreQueryFilters()),
                item => item.SyncedAt,
                item => item.IsActive,
                24)
        };

        return Ok(statuses);
    }
}

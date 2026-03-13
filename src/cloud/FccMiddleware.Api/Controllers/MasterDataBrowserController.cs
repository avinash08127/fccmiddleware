using System.Linq.Expressions;
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
    private readonly IDbContextFactory<FccMiddlewareDbContext> _dbFactory;
    private readonly PortalAccessResolver _accessResolver;

    public MasterDataBrowserController(
        FccMiddlewareDbContext db,
        IDbContextFactory<FccMiddlewareDbContext> dbFactory,
        PortalAccessResolver accessResolver)
    {
        _db = db;
        _dbFactory = dbFactory;
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
                Code = item.BusinessCode,
                Name = item.Name,
                CountryCode = item.CountryCode,
                CountryName = item.CountryName,
                CurrencyCode = item.CurrencyCode,
                OdooCompanyId = item.OdooCompanyId,
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

        // Run all 5 entity-type queries in parallel. Each call creates its own
        // DbContext via IDbContextFactory to avoid concurrent-access violations.
        var statuses = await Task.WhenAll(
            BuildEntityStats(
                "legal_entities",
                db => db.LegalEntities.IgnoreQueryFilters()
                    .Where(item => legalEntityIds == null || legalEntityIds.Contains(item.Id)),
                item => item.SyncedAt,
                item => item.IsActive,
                24, cancellationToken),
            BuildEntityStats(
                "sites",
                db => db.Sites.IgnoreQueryFilters()
                    .Where(item => legalEntityIds == null || legalEntityIds.Contains(item.LegalEntityId)),
                item => item.SyncedAt,
                item => item.IsActive,
                24, cancellationToken),
            BuildEntityStats(
                "pumps",
                db => db.Pumps.IgnoreQueryFilters()
                    .Where(item => legalEntityIds == null || legalEntityIds.Contains(item.LegalEntityId)),
                item => item.SyncedAt,
                item => item.IsActive,
                24, cancellationToken),
            BuildEntityStats(
                "products",
                db => db.Products.IgnoreQueryFilters()
                    .Where(item => legalEntityIds == null || legalEntityIds.Contains(item.LegalEntityId)),
                item => item.SyncedAt,
                item => item.IsActive,
                24, cancellationToken),
            BuildEntityStats(
                "operators",
                db => db.Operators.IgnoreQueryFilters()
                    .Where(item => legalEntityIds == null || legalEntityIds.Contains(item.LegalEntityId)),
                item => item.SyncedAt,
                item => item.IsActive,
                24, cancellationToken));

        return Ok(statuses);
    }

    // Computes aggregate stats for one entity type using server-side SQL (no full table scan).
    // Creates its own DbContext so callers can safely run multiple instances in parallel.
    private async Task<MasterDataSyncStatusDto> BuildEntityStats<T>(
        string entityType,
        Func<FccMiddlewareDbContext, IQueryable<T>> queryFactory,
        Expression<Func<T, DateTimeOffset>> syncedAtSelector,
        Expression<Func<T, bool>> isActiveSelector,
        int staleThresholdHours,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = queryFactory(db).AsNoTracking();

        var totalCount = await query.CountAsync(cancellationToken);
        DateTimeOffset? lastSync = null;
        var activeCount = 0;

        if (totalCount > 0)
        {
            lastSync = await query
                .Select(syncedAtSelector)
                .Select(d => (DateTimeOffset?)d)
                .MaxAsync(cancellationToken);
            activeCount = await query.CountAsync(isActiveSelector, cancellationToken);
        }

        return new MasterDataSyncStatusDto
        {
            EntityType = entityType,
            LastSyncAtUtc = lastSync,
            TotalActiveCount = activeCount,
            DeactivatedCount = totalCount - activeCount,
            ErrorCount = 0,
            IsStale = !lastSync.HasValue || lastSync.Value < DateTimeOffset.UtcNow.AddHours(-staleThresholdHours),
            StaleThresholdHours = staleThresholdHours
        };
    }
}

using FccMiddleware.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FccMiddleware.Api.Portal;

/// <summary>
/// Extension methods for building tenant-scoped portal queries.
/// Replaces the raw IgnoreQueryFilters() + manual filtering pattern
/// with a single call that ensures tenant scoping is always applied,
/// reducing the risk of data leakage if a new query omits the manual check.
/// </summary>
public static class PortalQueryExtensions
{
    /// <summary>
    /// Creates a read-only, tenant-scoped query for portal controllers.
    /// Bypasses EF Core global query filters (which only work for single-tenant contexts)
    /// and applies explicit tenant scoping based on the user's <see cref="PortalAccess"/>.
    /// </summary>
    /// <typeparam name="T">An entity implementing <see cref="ITenantScoped"/>.</typeparam>
    /// <param name="dbSet">The EF Core DbSet to query.</param>
    /// <param name="access">The resolved portal access for the current user.</param>
    /// <param name="legalEntityId">
    /// Optional: when provided, scopes to a single legal entity (after verifying access).
    /// When null, scopes to all legal entities the user can access.
    /// </param>
    /// <returns>A filtered, no-tracking queryable.</returns>
    public static IQueryable<T> ForPortal<T>(
        this DbSet<T> dbSet,
        PortalAccess access,
        Guid? legalEntityId = null)
        where T : class, ITenantScoped
    {
        var query = dbSet.IgnoreQueryFilters().AsNoTracking();

        if (legalEntityId.HasValue)
        {
            return query.Where(e => e.LegalEntityId == legalEntityId.Value);
        }

        if (!access.AllowAllLegalEntities)
        {
            return query.Where(e => access.ScopedLegalEntityIds.Contains(e.LegalEntityId));
        }

        return query;
    }
}

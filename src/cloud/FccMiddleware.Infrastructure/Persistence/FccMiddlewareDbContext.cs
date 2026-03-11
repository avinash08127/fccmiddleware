using Microsoft.EntityFrameworkCore;

namespace FccMiddleware.Infrastructure.Persistence;

/// <summary>
/// EF Core DbContext for FCC Middleware.
/// Connection string: ConnectionStrings:FccMiddleware in appsettings.
/// Multi-tenancy: a global query filter on LegalEntityId will be applied here (CB-1.x).
/// </summary>
public class FccMiddlewareDbContext : DbContext
{
    public FccMiddlewareDbContext(DbContextOptions<FccMiddlewareDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // TODO CB-1.x: Apply IEntityTypeConfiguration<T> from this assembly
        // modelBuilder.ApplyConfigurationsFromAssembly(typeof(FccMiddlewareDbContext).Assembly);

        // TODO CB-1.x: Add multi-tenancy global query filter, e.g.:
        // modelBuilder.Entity<TenantEntity>()
        //     .HasQueryFilter(e => e.LegalEntityId == _currentTenantId);
    }
}

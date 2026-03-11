using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FccMiddleware.Infrastructure.Persistence;

/// <summary>
/// EF Core DbContext for the FCC Middleware cloud backend.
///
/// Multi-tenancy: global query filters on LegalEntityId enforce tenant isolation on all
/// tenant-scoped entities. When ICurrentTenantProvider.CurrentLegalEntityId is null
/// (background workers, admin operations) the filter is bypassed, allowing cross-tenant queries.
///
/// Partitioned tables: <see cref="Transaction"/> and <see cref="AuditEvent"/> use composite PKs
/// (Id, CreatedAt) matching the PostgreSQL PARTITION BY RANGE (created_at) DDL.
/// Partition creation and management is handled by pg_partman outside EF Core migrations.
///
/// Outbox: <see cref="OutboxMessage"/> uses a bigint GENERATED ALWAYS AS IDENTITY column.
/// </summary>
public class FccMiddlewareDbContext : DbContext
{
    private readonly ICurrentTenantProvider _tenantProvider;

    public FccMiddlewareDbContext(
        DbContextOptions<FccMiddlewareDbContext> options,
        ICurrentTenantProvider tenantProvider)
        : base(options)
    {
        _tenantProvider = tenantProvider;
    }

    // -------------------------------------------------------------------------
    // Master data
    // -------------------------------------------------------------------------
    public DbSet<LegalEntity> LegalEntities => Set<LegalEntity>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Pump> Pumps => Set<Pump>();
    public DbSet<Nozzle> Nozzles => Set<Nozzle>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Operator> Operators => Set<Operator>();

    // -------------------------------------------------------------------------
    // Transactional
    // -------------------------------------------------------------------------
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<PreAuthRecord> PreAuthRecords => Set<PreAuthRecord>();

    // -------------------------------------------------------------------------
    // Configuration & registration
    // -------------------------------------------------------------------------
    public DbSet<FccConfig> FccConfigs => Set<FccConfig>();
    public DbSet<AgentRegistration> AgentRegistrations => Set<AgentRegistration>();

    // -------------------------------------------------------------------------
    // Audit & outbox
    // -------------------------------------------------------------------------
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all IEntityTypeConfiguration<T> from this assembly (Infrastructure).
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FccMiddlewareDbContext).Assembly);

        // -------------------------------------------------------------------------
        // Multi-tenancy: global query filters on LegalEntityId.
        // When CurrentLegalEntityId is null (background workers), the filter is
        // bypassed so cross-tenant queries are allowed.
        // Decision D1 in tier-1-4-database-schema-design.md.
        // -------------------------------------------------------------------------
        ApplyTenantFilters(modelBuilder);
    }

    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        // Each tenant-scoped entity gets a filter that enforces LegalEntityId equality.
        // The lambda captures _tenantProvider by reference; CurrentLegalEntityId is
        // evaluated at query time (not at OnModelCreating time).

        modelBuilder.Entity<Site>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<Pump>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<Nozzle>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<Product>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<Operator>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<Transaction>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<PreAuthRecord>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<FccConfig>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<AgentRegistration>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<AuditEvent>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        // LegalEntity and OutboxMessage have no LegalEntityId — no filter applied.
    }
}

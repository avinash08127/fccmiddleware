using FccMiddleware.Application.AgentConfig;
using FccMiddleware.Application.Ingestion;
using FccMiddleware.Application.MasterData;
using FccMiddleware.Application.PreAuth;
using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Application.Registration;
using FccMiddleware.Application.Telemetry;
using FccMiddleware.Application.Transactions;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Infrastructure.Deduplication;
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
public class FccMiddlewareDbContext : DbContext, IIngestDbContext, IDeduplicationDbContext, IPollTransactionsDbContext, IAcknowledgeTransactionsDbContext, IMasterDataSyncDbContext, IRegistrationDbContext, IAgentConfigDbContext, ITelemetryDbContext, IPreAuthDbContext, IReconciliationDbContext
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
    public DbSet<ReconciliationRecord> ReconciliationRecords => Set<ReconciliationRecord>();

    // -------------------------------------------------------------------------
    // Configuration & registration
    // -------------------------------------------------------------------------
    public DbSet<FccConfig> FccConfigs => Set<FccConfig>();
    public DbSet<AgentRegistration> AgentRegistrations => Set<AgentRegistration>();
    public DbSet<AgentTelemetrySnapshot> AgentTelemetrySnapshots => Set<AgentTelemetrySnapshot>();
    public DbSet<PortalSettings> PortalSettings => Set<PortalSettings>();
    public DbSet<LegalEntitySettingsOverride> LegalEntitySettingsOverrides => Set<LegalEntitySettingsOverride>();
    public DbSet<DeadLetterItem> DeadLetterItems => Set<DeadLetterItem>();

    // -------------------------------------------------------------------------
    // Audit & outbox
    // -------------------------------------------------------------------------
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    // -------------------------------------------------------------------------
    // API key management
    // -------------------------------------------------------------------------
    public DbSet<OdooApiKey>         OdooApiKeys         => Set<OdooApiKey>();
    public DbSet<DatabricksApiKey>   DatabricksApiKeys   => Set<DatabricksApiKey>();

    // -------------------------------------------------------------------------
    // Device registration & provisioning
    // -------------------------------------------------------------------------
    public DbSet<BootstrapToken>     BootstrapTokens     => Set<BootstrapToken>();
    public DbSet<DeviceRefreshToken> DeviceRefreshTokens => Set<DeviceRefreshToken>();

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

    // -------------------------------------------------------------------------
    // IIngestDbContext implementation
    // -------------------------------------------------------------------------

    void IIngestDbContext.AddTransaction(Transaction transaction) => Transactions.Add(transaction);

    void IIngestDbContext.AddOutboxMessage(OutboxMessage message) => OutboxMessages.Add(message);

    void IIngestDbContext.ClearTracked() => ChangeTracker.Clear();

    async Task<Guid?> IIngestDbContext.FindTransactionByDedupKeyAsync(
        string fccTransactionId, string siteCode, CancellationToken ct) =>
        await Transactions
            .IgnoreQueryFilters()
            .Where(t => t.FccTransactionId == fccTransactionId && t.SiteCode == siteCode)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

    async Task<bool> IIngestDbContext.HasFuzzyMatchAsync(
        Guid legalEntityId, string siteCode,
        int pumpNumber, int nozzleNumber,
        long amountMinorUnits,
        DateTimeOffset windowStart, DateTimeOffset windowEnd,
        CancellationToken ct) =>
        await Transactions
            .IgnoreQueryFilters()
            .AnyAsync(t =>
                t.LegalEntityId == legalEntityId
             && t.SiteCode == siteCode
             && t.PumpNumber == pumpNumber
             && t.NozzleNumber == nozzleNumber
             && t.AmountMinorUnits == amountMinorUnits
             && t.CompletedAt >= windowStart
             && t.CompletedAt <= windowEnd
             && t.Status == TransactionStatus.PENDING,
                ct);

    // -------------------------------------------------------------------------
    // IPollTransactionsDbContext implementation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Executes the Odoo poll query against the transactions table.
    /// Uses ix_transactions_odoo_poll partial index (status = 'PENDING').
    /// Keyset cursor: WHERE (created_at > cursorCreatedAt) OR
    ///                      (created_at = cursorCreatedAt AND id > cursorId)
    /// </summary>
    async Task<List<Application.Transactions.PollTransactionRecord>>
        IPollTransactionsDbContext.FetchPendingPageAsync(
            Guid legalEntityId,
            string? siteCode,
            int? pumpNumber,
            DateTimeOffset? from,
            DateTimeOffset? cursorCreatedAt,
            Guid? cursorId,
            int take,
            CancellationToken ct)
    {
        var query = Set<Transaction>()
            .IgnoreQueryFilters()
            .Where(t => t.LegalEntityId == legalEntityId
                     && t.Status == TransactionStatus.PENDING);

        if (!string.IsNullOrEmpty(siteCode))
            query = query.Where(t => t.SiteCode == siteCode);

        if (pumpNumber.HasValue)
            query = query.Where(t => t.PumpNumber == pumpNumber.Value);

        if (from.HasValue)
            query = query.Where(t => t.CreatedAt >= from.Value);

        if (cursorCreatedAt.HasValue && cursorId.HasValue)
        {
            var cCAt = cursorCreatedAt.Value;
            var cId  = cursorId.Value;
            query = query.Where(t =>
                t.CreatedAt > cCAt
                || (t.CreatedAt == cCAt && t.Id.CompareTo(cId) > 0));
        }

        return await query
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Take(take)
            .Select(t => new Application.Transactions.PollTransactionRecord
            {
                Id                     = t.Id,
                FccTransactionId       = t.FccTransactionId,
                SiteCode               = t.SiteCode,
                PumpNumber             = t.PumpNumber,
                NozzleNumber           = t.NozzleNumber,
                ProductCode            = t.ProductCode,
                VolumeMicrolitres      = t.VolumeMicrolitres,
                AmountMinorUnits       = t.AmountMinorUnits,
                UnitPriceMinorPerLitre = t.UnitPriceMinorPerLitre,
                CurrencyCode           = t.CurrencyCode,
                StartedAt              = t.StartedAt,
                CompletedAt            = t.CompletedAt,
                CreatedAt              = t.CreatedAt,
                Status                 = t.Status.ToString(),
                CorrelationId          = t.CorrelationId,
                FiscalReceiptNumber    = t.FiscalReceiptNumber,
                AttendantId            = t.AttendantId,
                IsStale                = t.IsStale,
                FccVendor              = t.FccVendor.ToString(),
                IngestionSource        = t.IngestionSource.ToString()
            })
            .ToListAsync(ct);
    }

    async Task<List<string>> IPollTransactionsDbContext.FetchSyncedTransactionIdsAsync(
        Guid legalEntityId,
        string siteCode,
        DateTimeOffset since,
        CancellationToken ct) =>
        await Set<Transaction>()
            .IgnoreQueryFilters()
            .Where(t => t.LegalEntityId == legalEntityId
                     && t.SiteCode == siteCode
                     && t.Status == TransactionStatus.SYNCED_TO_ODOO
                     && t.SyncedToOdooAt.HasValue
                     && t.SyncedToOdooAt.Value >= since)
            .OrderBy(t => t.SyncedToOdooAt)
            .ThenBy(t => t.Id)
            .Select(t => t.FccTransactionId)
            .ToListAsync(ct);

    // -------------------------------------------------------------------------
    // IAcknowledgeTransactionsDbContext implementation
    // -------------------------------------------------------------------------

    async Task<List<Transaction>> IAcknowledgeTransactionsDbContext.FindTransactionsByIdsAsync(
        IReadOnlyList<Guid> ids,
        Guid legalEntityId,
        CancellationToken ct) =>
        await Set<Transaction>()
            .IgnoreQueryFilters()
            .Where(t => t.LegalEntityId == legalEntityId && ids.Contains(t.Id))
            .ToListAsync(ct);

    void IAcknowledgeTransactionsDbContext.AddOutboxMessage(OutboxMessage message) =>
        OutboxMessages.Add(message);

    // -------------------------------------------------------------------------
    // IDeduplicationDbContext implementation
    // -------------------------------------------------------------------------

    async Task<Guid?> IDeduplicationDbContext.FindTransactionIdByDedupKeyAsync(
        string fccTransactionId, string siteCode, CancellationToken ct) =>
        await Transactions
            .IgnoreQueryFilters()
            .Where(t => t.FccTransactionId == fccTransactionId && t.SiteCode == siteCode)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

    // -------------------------------------------------------------------------

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

        modelBuilder.Entity<ReconciliationRecord>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<FccConfig>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<AgentRegistration>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<AgentTelemetrySnapshot>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<AuditEvent>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<BootstrapToken>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<LegalEntitySettingsOverride>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        modelBuilder.Entity<DeadLetterItem>()
            .HasQueryFilter(e => !_tenantProvider.CurrentLegalEntityId.HasValue
                || e.LegalEntityId == _tenantProvider.CurrentLegalEntityId.Value);

        // LegalEntity, OutboxMessage, DeviceRefreshToken have no LegalEntityId — no filter applied.
    }

    // -------------------------------------------------------------------------
    // IMasterDataSyncDbContext implementation
    // All reads use IgnoreQueryFilters() so the sync logic sees all records
    // regardless of current tenant context.
    // -------------------------------------------------------------------------

    Task<List<LegalEntity>> IMasterDataSyncDbContext.GetLegalEntitiesByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        Set<LegalEntity>().IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).ToListAsync(ct);

    Task<List<Guid>> IMasterDataSyncDbContext.GetActiveLegalEntityIdsAsync(CancellationToken ct) =>
        Set<LegalEntity>().IgnoreQueryFilters().Where(e => e.IsActive).Select(e => e.Id).ToListAsync(ct);

    Task<List<Site>> IMasterDataSyncDbContext.GetSitesByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        Set<Site>().IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).ToListAsync(ct);

    Task<List<Guid>> IMasterDataSyncDbContext.GetActiveSiteIdsAsync(CancellationToken ct) =>
        Set<Site>().IgnoreQueryFilters().Where(e => e.IsActive).Select(e => e.Id).ToListAsync(ct);

    Task<List<Pump>> IMasterDataSyncDbContext.GetPumpsByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        Set<Pump>().IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).ToListAsync(ct);

    Task<List<Guid>> IMasterDataSyncDbContext.GetActivePumpIdsAsync(CancellationToken ct) =>
        Set<Pump>().IgnoreQueryFilters().Where(e => e.IsActive).Select(e => e.Id).ToListAsync(ct);

    async Task<Dictionary<string, Site>> IMasterDataSyncDbContext.GetSitesByCodesAsync(IReadOnlyList<string> siteCodes, CancellationToken ct)
    {
        var sites = await Set<Site>().IgnoreQueryFilters()
            .Where(s => siteCodes.Contains(s.SiteCode))
            .ToListAsync(ct);
        return sites.ToDictionary(s => s.SiteCode);
    }

    Task<List<Nozzle>> IMasterDataSyncDbContext.GetNozzlesByPumpIdsAsync(IReadOnlyList<Guid> pumpIds, CancellationToken ct) =>
        Set<Nozzle>().IgnoreQueryFilters().Where(n => pumpIds.Contains(n.PumpId)).ToListAsync(ct);

    Task<List<Product>> IMasterDataSyncDbContext.GetProductsByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        Set<Product>().IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).ToListAsync(ct);

    Task<List<Guid>> IMasterDataSyncDbContext.GetActiveProductIdsAsync(Guid legalEntityId, CancellationToken ct) =>
        Set<Product>().IgnoreQueryFilters()
            .Where(e => e.LegalEntityId == legalEntityId && e.IsActive)
            .Select(e => e.Id)
            .ToListAsync(ct);

    Task<Product?> IMasterDataSyncDbContext.FindProductByCodeAsync(Guid legalEntityId, string productCode, CancellationToken ct) =>
        Set<Product>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.LegalEntityId == legalEntityId && p.ProductCode == productCode, ct);

    Task<List<Operator>> IMasterDataSyncDbContext.GetOperatorsByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        Set<Operator>().IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).ToListAsync(ct);

    Task<List<Guid>> IMasterDataSyncDbContext.GetActiveOperatorIdsAsync(Guid legalEntityId, CancellationToken ct) =>
        Set<Operator>().IgnoreQueryFilters()
            .Where(e => e.LegalEntityId == legalEntityId && e.IsActive)
            .Select(e => e.Id)
            .ToListAsync(ct);

    void IMasterDataSyncDbContext.AddLegalEntity(LegalEntity entity) => LegalEntities.Add(entity);
    void IMasterDataSyncDbContext.AddSite(Site entity)               => Sites.Add(entity);
    void IMasterDataSyncDbContext.AddPump(Pump entity)               => Pumps.Add(entity);
    void IMasterDataSyncDbContext.AddNozzle(Nozzle entity)           => Nozzles.Add(entity);
    void IMasterDataSyncDbContext.AddProduct(Product entity)         => Products.Add(entity);
    void IMasterDataSyncDbContext.AddOperator(Operator entity)       => Operators.Add(entity);
    void IMasterDataSyncDbContext.AddOutboxMessage(OutboxMessage msg) => OutboxMessages.Add(msg);

    // -------------------------------------------------------------------------
    // IRegistrationDbContext implementation
    // All reads use IgnoreQueryFilters() so registration logic works without
    // tenant context (bootstrap token is unauthenticated).
    // -------------------------------------------------------------------------

    async Task<BootstrapToken?> IRegistrationDbContext.FindBootstrapTokenByHashAsync(string tokenHash, CancellationToken ct) =>
        await Set<BootstrapToken>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    async Task<Site?> IRegistrationDbContext.FindSiteBySiteCodeAsync(string siteCode, CancellationToken ct) =>
        await Set<Site>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.SiteCode == siteCode, ct);

    async Task<AgentRegistration?> IRegistrationDbContext.FindActiveAgentForSiteAsync(Guid siteId, CancellationToken ct) =>
        await Set<AgentRegistration>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.SiteId == siteId && a.IsActive, ct);

    async Task<AgentRegistration?> IRegistrationDbContext.FindAgentByIdAsync(Guid deviceId, CancellationToken ct) =>
        await Set<AgentRegistration>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == deviceId, ct);

    async Task<DeviceRefreshToken?> IRegistrationDbContext.FindRefreshTokenByHashAsync(string tokenHash, CancellationToken ct) =>
        await Set<DeviceRefreshToken>()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    async Task<List<DeviceRefreshToken>> IRegistrationDbContext.GetActiveRefreshTokensForDeviceAsync(Guid deviceId, CancellationToken ct) =>
        await Set<DeviceRefreshToken>()
            .Where(t => t.DeviceId == deviceId && t.RevokedAt == null)
            .ToListAsync(ct);

    void IRegistrationDbContext.AddAgentRegistration(AgentRegistration registration) =>
        AgentRegistrations.Add(registration);

    void IRegistrationDbContext.AddBootstrapToken(BootstrapToken token) =>
        BootstrapTokens.Add(token);

    void IRegistrationDbContext.AddDeviceRefreshToken(DeviceRefreshToken token) =>
        DeviceRefreshTokens.Add(token);

    void IRegistrationDbContext.AddAuditEvent(AuditEvent auditEvent) =>
        AuditEvents.Add(auditEvent);

    async Task<bool> IRegistrationDbContext.TrySaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // IAgentConfigDbContext implementation
    // Eager-loads Site → Pumps → Nozzles → Products and LegalEntity so the
    // handler can build a complete SiteConfig without additional queries.
    // -------------------------------------------------------------------------

    async Task<FccConfig?> IAgentConfigDbContext.GetFccConfigWithSiteDataAsync(
        string siteCode, Guid legalEntityId, CancellationToken ct) =>
        await Set<FccConfig>().IgnoreQueryFilters()
            .Include(fc => fc.LegalEntity)
            .Include(fc => fc.Site)
                .ThenInclude(s => s.Pumps.Where(p => p.IsActive))
                    .ThenInclude(p => p.Nozzles.Where(n => n.IsActive))
                        .ThenInclude(n => n.Product)
            .Where(fc => fc.LegalEntityId == legalEntityId
                      && fc.Site.SiteCode == siteCode
                      && fc.IsActive)
            .FirstOrDefaultAsync(ct);

    async Task<AgentRegistration?> IAgentConfigDbContext.FindAgentByDeviceIdAsync(
        Guid deviceId, CancellationToken ct) =>
        await Set<AgentRegistration>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == deviceId, ct);

    // -------------------------------------------------------------------------
    // ITelemetryDbContext implementation
    // -------------------------------------------------------------------------

    async Task<AgentRegistration?> ITelemetryDbContext.FindAgentByDeviceIdAsync(
        Guid deviceId, CancellationToken ct) =>
        await Set<AgentRegistration>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == deviceId, ct);

    Task<bool> ITelemetryDbContext.HasAuditEventAsync(Guid correlationId, string eventType, CancellationToken ct) =>
        Set<AuditEvent>().IgnoreQueryFilters()
            .AnyAsync(a => a.CorrelationId == correlationId && a.EventType == eventType, ct);

    void ITelemetryDbContext.AddAuditEvent(AuditEvent auditEvent) =>
        AuditEvents.Add(auditEvent);

    Task<AgentTelemetrySnapshot?> ITelemetryDbContext.FindTelemetrySnapshotByDeviceIdAsync(
        Guid deviceId,
        CancellationToken ct) =>
        Set<AgentTelemetrySnapshot>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(snapshot => snapshot.DeviceId == deviceId, ct);

    void ITelemetryDbContext.AddTelemetrySnapshot(AgentTelemetrySnapshot snapshot) =>
        AgentTelemetrySnapshots.Add(snapshot);

    // -------------------------------------------------------------------------
    // IPreAuthDbContext implementation
    // -------------------------------------------------------------------------

    async Task<PreAuthRecord?> IPreAuthDbContext.FindByDedupKeyAsync(
        string odooOrderId, string siteCode, CancellationToken ct) =>
        await Set<PreAuthRecord>()
            .IgnoreQueryFilters()
            .Where(p => p.OdooOrderId == odooOrderId && p.SiteCode == siteCode)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

    async Task<PreAuthRecord?> IPreAuthDbContext.FindByIdAsync(
        Guid preAuthId,
        Guid legalEntityId,
        CancellationToken ct) =>
        await Set<PreAuthRecord>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.Id == preAuthId && p.LegalEntityId == legalEntityId,
                ct);

    void IPreAuthDbContext.AddPreAuthRecord(PreAuthRecord record) =>
        PreAuthRecords.Add(record);

    void IPreAuthDbContext.AddOutboxMessage(OutboxMessage message) =>
        OutboxMessages.Add(message);

    void IPreAuthDbContext.ClearTracked() => ChangeTracker.Clear();

    // -------------------------------------------------------------------------
    // IReconciliationDbContext implementation
    // -------------------------------------------------------------------------

    Task<ReconciliationRecord?> IReconciliationDbContext.FindByIdAsync(
        Guid reconciliationId,
        CancellationToken ct) =>
        Set<ReconciliationRecord>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == reconciliationId, ct);

    Task<ReconciliationRecord?> IReconciliationDbContext.FindByTransactionIdAsync(
        Guid transactionId,
        CancellationToken ct) =>
        Set<ReconciliationRecord>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TransactionId == transactionId, ct);

    Task<Transaction?> IReconciliationDbContext.FindTransactionByIdAsync(
        Guid transactionId,
        CancellationToken ct) =>
        Set<Transaction>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);

    async Task<List<ReconciliationExceptionListItem>> IReconciliationDbContext.FetchExceptionsPageAsync(
        Guid? legalEntityId,
        IReadOnlyCollection<Guid> scopedLegalEntityIds,
        bool allowAllLegalEntities,
        string? siteCode,
        IReadOnlyCollection<ReconciliationStatus> statuses,
        DateTimeOffset? since,
        DateTimeOffset? cursorCreatedAt,
        Guid? cursorId,
        int take,
        CancellationToken ct)
    {
        var query = Set<ReconciliationRecord>()
            .IgnoreQueryFilters()
            .AsQueryable();

        if (legalEntityId.HasValue)
        {
            query = query.Where(r => r.LegalEntityId == legalEntityId.Value);
        }
        else if (!allowAllLegalEntities)
        {
            query = query.Where(r => scopedLegalEntityIds.Contains(r.LegalEntityId));
        }

        if (!string.IsNullOrWhiteSpace(siteCode))
        {
            query = query.Where(r => r.SiteCode == siteCode);
        }

        if (statuses.Count > 0)
        {
            query = query.Where(r => statuses.Contains(r.Status));
        }

        if (since.HasValue)
        {
            query = query.Where(r => r.CreatedAt >= since.Value);
        }

        if (cursorCreatedAt.HasValue && cursorId.HasValue)
        {
            var createdAt = cursorCreatedAt.Value;
            var id = cursorId.Value;

            query = query.Where(r =>
                r.CreatedAt > createdAt
                || (r.CreatedAt == createdAt && r.Id.CompareTo(id) > 0));
        }

        return await query
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Take(take)
            .Select(r => new ReconciliationExceptionListItem(
                r.Id,
                r.Status,
                r.SiteCode,
                r.LegalEntityId,
                r.PumpNumber,
                r.NozzleNumber,
                r.AuthorizedAmountMinorUnits,
                r.ActualAmountMinorUnits,
                r.VarianceMinorUnits,
                r.VariancePercent,
                r.MatchMethod,
                r.AmbiguityFlag,
                r.CreatedAt,
                r.LastMatchAttemptAt))
            .ToListAsync(ct);
    }

    async Task<List<ReconciliationRetryWorkItem>> IReconciliationDbContext.FindDueUnmatchedRetriesAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken ct)
    {
        var phase1Threshold = now.AddMinutes(-5);
        var phase2Threshold = now.AddHours(-1);
        var phaseBoundary = now.AddMinutes(-60);
        var giveUpBoundary = now.AddHours(-24);

        var records = await Set<ReconciliationRecord>()
            .IgnoreQueryFilters()
            .Where(r =>
                r.Status == ReconciliationStatus.UNMATCHED
                && (
                    (r.CreatedAt >= phaseBoundary && r.LastMatchAttemptAt <= phase1Threshold)
                    || (r.CreatedAt < phaseBoundary && r.CreatedAt >= giveUpBoundary && r.LastMatchAttemptAt <= phase2Threshold)
                    || (r.CreatedAt < giveUpBoundary && r.EscalatedAtUtc == null)))
            .OrderBy(r => r.LastMatchAttemptAt)
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Take(batchSize)
            .Join(
                Set<Transaction>().IgnoreQueryFilters(),
                reconciliation => reconciliation.TransactionId,
                transaction => transaction.Id,
                (reconciliation, transaction) => new ReconciliationRetryWorkItem(reconciliation, transaction))
            .ToListAsync(ct);

        return records;
    }

    async Task<ReconciliationSiteContext?> IReconciliationDbContext.FindSiteContextAsync(
        Guid legalEntityId,
        string siteCode,
        ReconciliationOptions defaults,
        CancellationToken ct)
    {
        var site = await Set<Site>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.LegalEntityId == legalEntityId && s.SiteCode == siteCode,
                ct);

        if (site is null)
        {
            return null;
        }

        var legalEntity = await Set<LegalEntity>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(le => le.Id == legalEntityId, ct);

        if (legalEntity is null)
        {
            return null;
        }

        return new ReconciliationSiteContext(
            legalEntityId,
            siteCode,
            new ReconciliationSettings(
                site.SiteUsesPreAuth,
                site.AmountTolerancePercent
                    ?? legalEntity.AmountTolerancePercent
                    ?? defaults.DefaultAmountTolerancePercent,
                site.AmountToleranceAbsolute
                    ?? legalEntity.AmountToleranceAbsolute
                    ?? defaults.DefaultAmountToleranceAbsolute,
                site.TimeWindowMinutes
                    ?? legalEntity.TimeWindowMinutes
                    ?? defaults.DefaultTimeWindowMinutes));
    }

    Task<List<PreAuthRecord>> IReconciliationDbContext.FindCorrelationCandidatesAsync(
        Guid legalEntityId,
        string siteCode,
        string fccCorrelationId,
        CancellationToken ct) =>
        Set<PreAuthRecord>()
            .IgnoreQueryFilters()
            .Where(p =>
                p.LegalEntityId == legalEntityId
                && p.SiteCode == siteCode
                && p.FccCorrelationId == fccCorrelationId
                && (p.Status == PreAuthStatus.AUTHORIZED || p.Status == PreAuthStatus.DISPENSING)
                && p.MatchedTransactionId == null)
            .ToListAsync(ct);

    Task<List<PreAuthRecord>> IReconciliationDbContext.FindPumpNozzleTimeCandidatesAsync(
        Guid legalEntityId,
        string siteCode,
        int pumpNumber,
        int nozzleNumber,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken ct) =>
        Set<PreAuthRecord>()
            .IgnoreQueryFilters()
            .Where(p =>
                p.LegalEntityId == legalEntityId
                && p.SiteCode == siteCode
                && p.PumpNumber == pumpNumber
                && p.NozzleNumber == nozzleNumber
                && p.AuthorizedAt.HasValue
                && p.AuthorizedAt.Value >= windowStart
                && p.AuthorizedAt.Value <= windowEnd
                && (p.Status == PreAuthStatus.AUTHORIZED || p.Status == PreAuthStatus.DISPENSING)
                && p.MatchedTransactionId == null)
            .ToListAsync(ct);

    Task<List<PreAuthRecord>> IReconciliationDbContext.FindOdooOrderCandidatesAsync(
        Guid legalEntityId,
        string siteCode,
        string odooOrderId,
        CancellationToken ct) =>
        Set<PreAuthRecord>()
            .IgnoreQueryFilters()
            .Where(p =>
                p.LegalEntityId == legalEntityId
                && p.SiteCode == siteCode
                && p.OdooOrderId == odooOrderId
                && (p.Status == PreAuthStatus.AUTHORIZED || p.Status == PreAuthStatus.DISPENSING)
                && p.MatchedTransactionId == null)
            .ToListAsync(ct);

    void IReconciliationDbContext.AddReconciliationRecord(ReconciliationRecord record) =>
        ReconciliationRecords.Add(record);
}

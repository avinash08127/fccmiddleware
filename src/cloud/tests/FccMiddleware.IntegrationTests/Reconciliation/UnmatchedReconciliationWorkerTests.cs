using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Application.Observability;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Infrastructure.Events;
using FccMiddleware.Infrastructure.Persistence;
using FccMiddleware.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FccMiddleware.IntegrationTests.Reconciliation;

public sealed class UnmatchedReconciliationWorkerTests
{
    [Fact]
    public async Task ProcessBatchAsync_RespectsRetryCadence_AndTransitionsDeferredMatch()
    {
        var services = BuildServices();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var legalEntityId = await SeedReconciliationContextAsync(db, "SITE-RECON-001");

        var now = DateTimeOffset.UtcNow;
        var dueRetryTx = CreateTransaction(legalEntityId, "SITE-RECON-001", "CORR-DUE", now.AddMinutes(-30));
        var dueRetryRecord = CreateUnmatchedRecord(dueRetryTx, now.AddMinutes(-30), now.AddMinutes(-6));
        var dueRetryPreAuth = CreatePreAuth(legalEntityId, "SITE-RECON-001", "ODOO-DUE", "CORR-DUE", 5000, now.AddMinutes(-32));

        var notDuePhase1Tx = CreateTransaction(legalEntityId, "SITE-RECON-001", "CORR-NOT-DUE-1", now.AddMinutes(-20));
        var notDuePhase1Record = CreateUnmatchedRecord(notDuePhase1Tx, now.AddMinutes(-20), now.AddMinutes(-2));

        var duePhase2Tx = CreateTransaction(legalEntityId, "SITE-RECON-001", "CORR-DUE-2", now.AddHours(-2));
        var duePhase2Record = CreateUnmatchedRecord(duePhase2Tx, now.AddHours(-2), now.AddMinutes(-61));

        var notDuePhase2Tx = CreateTransaction(legalEntityId, "SITE-RECON-001", "CORR-NOT-DUE-2", now.AddHours(-2));
        var notDuePhase2Record = CreateUnmatchedRecord(notDuePhase2Tx, now.AddHours(-2), now.AddMinutes(-30));

        db.Transactions.AddRange(dueRetryTx, notDuePhase1Tx, duePhase2Tx, notDuePhase2Tx);
        db.ReconciliationRecords.AddRange(dueRetryRecord, notDuePhase1Record, duePhase2Record, notDuePhase2Record);
        db.PreAuthRecords.Add(dueRetryPreAuth);
        await db.SaveChangesAsync();

        var phase2LastAttemptBefore = duePhase2Record.LastMatchAttemptAt;
        var phase1NotDueLastAttemptBefore = notDuePhase1Record.LastMatchAttemptAt;
        var phase2NotDueLastAttemptBefore = notDuePhase2Record.LastMatchAttemptAt;

        var worker = CreateWorker(services);

        var processed = await worker.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(2);

        await db.Entry(dueRetryRecord).ReloadAsync();
        await db.Entry(notDuePhase1Record).ReloadAsync();
        await db.Entry(duePhase2Record).ReloadAsync();
        await db.Entry(notDuePhase2Record).ReloadAsync();
        await db.Entry(dueRetryPreAuth).ReloadAsync();
        await db.Entry(dueRetryTx).ReloadAsync();

        dueRetryRecord.Status.Should().Be(ReconciliationStatus.MATCHED);
        dueRetryRecord.PreAuthId.Should().Be(dueRetryPreAuth.Id);
        dueRetryTx.ReconciliationStatus.Should().Be(ReconciliationStatus.MATCHED);
        dueRetryPreAuth.Status.Should().Be(PreAuthStatus.COMPLETED);

        duePhase2Record.Status.Should().Be(ReconciliationStatus.UNMATCHED);
        duePhase2Record.LastMatchAttemptAt.Should().BeAfter(phase2LastAttemptBefore);

        notDuePhase1Record.LastMatchAttemptAt.Should().Be(phase1NotDueLastAttemptBefore);
        notDuePhase2Record.LastMatchAttemptAt.Should().Be(phase2NotDueLastAttemptBefore);

        var matchedEventCount = await db.OutboxMessages.CountAsync(m => m.EventType == "ReconciliationMatched");
        matchedEventCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessBatchAsync_AgedRecord_EscalatesOnceAndStopsRetrying()
    {
        var services = BuildServices();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var legalEntityId = await SeedReconciliationContextAsync(db, "SITE-RECON-AGED");

        var now = DateTimeOffset.UtcNow;
        var tx = CreateTransaction(legalEntityId, "SITE-RECON-AGED", "CORR-AGED", now.AddHours(-25));
        var record = CreateUnmatchedRecord(tx, now.AddHours(-25), now.AddHours(-2));

        db.Transactions.Add(tx);
        db.ReconciliationRecords.Add(record);
        await db.SaveChangesAsync();

        var worker = CreateWorker(services);

        var processedFirst = await worker.ProcessBatchAsync(CancellationToken.None);
        var processedSecond = await worker.ProcessBatchAsync(CancellationToken.None);

        processedFirst.Should().Be(1);
        processedSecond.Should().Be(0);

        await db.Entry(record).ReloadAsync();
        record.EscalatedAtUtc.Should().NotBeNull();
        record.Status.Should().Be(ReconciliationStatus.UNMATCHED);

        var agedEventCount = await db.OutboxMessages.CountAsync(m => m.EventType == "ReconciliationUnmatchedAged");
        agedEventCount.Should().Be(1);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();

        services.AddSingleton<ICurrentTenantProvider>(new TestTenantProvider());
        services.AddDbContext<FccMiddlewareDbContext>(opts =>
            opts.UseInMemoryDatabase(databaseName));
        services.AddScoped<IReconciliationDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
        services.AddScoped<IEventPublisher, OutboxEventPublisher>();
        services.AddSingleton<IObservabilityMetrics, NoOpObservabilityMetrics>();
        services.AddScoped<ReconciliationMatchingService>();
        services.AddLogging();
        services.Configure<ReconciliationOptions>(options =>
        {
            options.DefaultAmountTolerancePercent = 2.0m;
            options.DefaultAmountToleranceAbsolute = 100;
            options.DefaultTimeWindowMinutes = 15;
        });

        return services.BuildServiceProvider();
    }

    private static UnmatchedReconciliationWorker CreateWorker(IServiceProvider services) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<UnmatchedReconciliationWorker>.Instance,
            Options.Create(new UnmatchedReconciliationWorkerOptions { BatchSize = 50 }));

    private sealed class NoOpObservabilityMetrics : IObservabilityMetrics
    {
        public void RecordApplicationError(string category, string route, int count = 1) { }
        public void RecordEdgeAgentOfflineHours(Guid legalEntityId, string siteCode, Guid deviceId, double offlineHours) { }
        public void RecordEdgeBufferDepth(Guid legalEntityId, string siteCode, Guid deviceId, int pendingUploadCount) { }
        public void RecordEdgeSyncLag(Guid legalEntityId, string siteCode, Guid deviceId, double syncLagHours) { }
        public void RecordFccHeartbeatAge(Guid legalEntityId, string siteCode, Guid deviceId, double heartbeatAgeMinutes) { }
        public void RecordIngestionFailure(string source, string category, string siteCode, string vendor, int count = 1) { }
        public void RecordIngestionSuccess(string source, string siteCode, string vendor, int count = 1) { }
        public void RecordOdooPollLatency(Guid legalEntityId, double latencyMs, int transactionCount) { }
        public void RecordReconciliationMatchRate(Guid legalEntityId, string siteCode, string matchMethod, bool matched) { }
        public void RecordReconciliationSkipped(Guid legalEntityId, string siteCode, string reason) { }
        public void RecordStaleTransactionCount(int staleCount) { }
    }

    private static async Task<Guid> SeedReconciliationContextAsync(FccMiddlewareDbContext db, string siteCode)
    {
        var legalEntityId = Guid.NewGuid();

        db.LegalEntities.Add(new LegalEntity
        {
            Id = legalEntityId,
            BusinessCode = $"GH-{siteCode}",
            CountryCode = "GH",
            CountryName = "Ghana",
            Name = $"Recon {siteCode}",
            CurrencyCode = "GHS",
            TaxAuthorityCode = "GRA",
            FiscalizationRequired = false,
            DefaultTimezone = "Africa/Accra",
            OdooCompanyId = $"ODOO-GH-{siteCode}",
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AmountTolerancePercent = 2.0m,
            AmountToleranceAbsolute = 100,
            TimeWindowMinutes = 15
        });

        db.Sites.Add(new Site
        {
            Id = Guid.NewGuid(),
            LegalEntityId = legalEntityId,
            SiteCode = siteCode,
            SiteName = $"{siteCode} Station",
            OperatingModel = SiteOperatingModel.COCO,
            CompanyTaxPayerId = $"TIN-{siteCode}",
            SiteUsesPreAuth = true,
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
        return legalEntityId;
    }

    private static Transaction CreateTransaction(
        Guid legalEntityId,
        string siteCode,
        string correlationId,
        DateTimeOffset completedAt) => new()
    {
        Id = Guid.NewGuid(),
        LegalEntityId = legalEntityId,
        FccTransactionId = $"TX-{Guid.NewGuid():N}",
        SiteCode = siteCode,
        PumpNumber = 1,
        NozzleNumber = 1,
        ProductCode = "PMS",
        VolumeMicrolitres = 10_000_000,
        AmountMinorUnits = 5000,
        UnitPriceMinorPerLitre = 500,
        CurrencyCode = "GHS",
        StartedAt = completedAt.AddMinutes(-1),
        CompletedAt = completedAt,
        FccVendor = FccVendor.DOMS,
        Status = TransactionStatus.PENDING,
        IngestionSource = IngestionSource.FCC_PUSH,
        OdooOrderId = $"ODOO-{Guid.NewGuid():N}",
        CorrelationId = Guid.NewGuid(),
        FccCorrelationId = correlationId,
        CreatedAt = completedAt,
        UpdatedAt = completedAt
    };

    private static ReconciliationRecord CreateUnmatchedRecord(
        Transaction transaction,
        DateTimeOffset createdAt,
        DateTimeOffset lastAttemptAt) => new()
    {
        Id = Guid.NewGuid(),
        LegalEntityId = transaction.LegalEntityId,
        SiteCode = transaction.SiteCode,
        TransactionId = transaction.Id,
        OdooOrderId = transaction.OdooOrderId,
        PumpNumber = transaction.PumpNumber,
        NozzleNumber = transaction.NozzleNumber,
        ActualAmountMinorUnits = transaction.AmountMinorUnits,
        MatchMethod = "NONE",
        Status = ReconciliationStatus.UNMATCHED,
        CreatedAt = createdAt,
        LastMatchAttemptAt = lastAttemptAt,
        UpdatedAt = lastAttemptAt
    };

    private static PreAuthRecord CreatePreAuth(
        Guid legalEntityId,
        string siteCode,
        string odooOrderId,
        string correlationId,
        long amountMinorUnits,
        DateTimeOffset authorizedAt) => new()
    {
        Id = Guid.NewGuid(),
        LegalEntityId = legalEntityId,
        SiteCode = siteCode,
        OdooOrderId = odooOrderId,
        PumpNumber = 1,
        NozzleNumber = 1,
        ProductCode = "PMS",
        CurrencyCode = "GHS",
        RequestedAmountMinorUnits = amountMinorUnits,
        AuthorizedAmountMinorUnits = amountMinorUnits,
        Status = PreAuthStatus.AUTHORIZED,
        RequestedAt = authorizedAt.AddMinutes(-2),
        AuthorizedAt = authorizedAt,
        ExpiresAt = authorizedAt.AddMinutes(30),
        CreatedAt = authorizedAt.AddMinutes(-2),
        UpdatedAt = authorizedAt,
        FccCorrelationId = correlationId
    };

    private sealed class TestTenantProvider : ICurrentTenantProvider
    {
        public Guid? CurrentLegalEntityId => null;
    }
}

using FccMiddleware.Application.Observability;
using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Infrastructure.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FccMiddleware.IntegrationTests.Reconciliation;

public sealed class UnmatchedReconciliationWorkerCachingTests
{
    [Fact]
    public async Task ProcessBatchAsync_ReusesSiteContextWithinBatch()
    {
        var legalEntityId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        const string siteCode = "SITE-BATCH-1";
        var siteContext = new ReconciliationSiteContext(
            legalEntityId,
            siteCode,
            new ReconciliationSettings(
                SiteUsesPreAuth: true,
                AmountTolerancePercent: 2.0m,
                AmountToleranceAbsolute: 100,
                TimeWindowMinutes: 15));

        var db = Substitute.For<IReconciliationDbContext>();
        var eventPublisher = Substitute.For<IEventPublisher>();
        var metrics = Substitute.For<IObservabilityMetrics>();
        var tx1 = CreateTransaction(legalEntityId, siteCode, "CORR-1");
        var tx2 = CreateTransaction(legalEntityId, siteCode, "CORR-2");
        var record1 = CreateUnmatchedRecord(tx1);
        var record2 = CreateUnmatchedRecord(tx2);
        var preAuth1 = CreatePreAuth(legalEntityId, siteCode, "CORR-1");
        var preAuth2 = CreatePreAuth(legalEntityId, siteCode, "CORR-2");

        db.FindDueUnmatchedRetriesAsync(Arg.Any<DateTimeOffset>(), 50, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ReconciliationRetryWorkItem(record1, tx1),
                new ReconciliationRetryWorkItem(record2, tx2)
            ]);
        db.FindSiteContextAsync(legalEntityId, siteCode, Arg.Any<ReconciliationOptions>(), Arg.Any<CancellationToken>())
            .Returns(siteContext);
        db.FindCorrelationCandidatesAsync(legalEntityId, siteCode, "CORR-1", Arg.Any<CancellationToken>())
            .Returns([preAuth1]);
        db.FindCorrelationCandidatesAsync(legalEntityId, siteCode, "CORR-2", Arg.Any<CancellationToken>())
            .Returns([preAuth2]);
        db.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);

        using var services = BuildServices(db, eventPublisher, metrics);
        var worker = new UnmatchedReconciliationWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<UnmatchedReconciliationWorker>.Instance,
            Options.Create(new UnmatchedReconciliationWorkerOptions { BatchSize = 50 }));

        var processed = await worker.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(2);
        _ = db.Received(1).FindSiteContextAsync(
            legalEntityId,
            siteCode,
            Arg.Any<ReconciliationOptions>(),
            Arg.Any<CancellationToken>());
        record1.Status.Should().Be(ReconciliationStatus.MATCHED);
        record2.Status.Should().Be(ReconciliationStatus.MATCHED);
        tx1.PreAuthId.Should().Be(preAuth1.Id);
        tx2.PreAuthId.Should().Be(preAuth2.Id);
    }

    private static ServiceProvider BuildServices(
        IReconciliationDbContext db,
        IEventPublisher eventPublisher,
        IObservabilityMetrics metrics)
    {
        var services = new ServiceCollection();

        services.AddScoped<IReconciliationDbContext>(_ => db);
        services.AddScoped<IEventPublisher>(_ => eventPublisher);
        services.AddSingleton<IObservabilityMetrics>(metrics);
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

    private static Transaction CreateTransaction(
        Guid legalEntityId,
        string siteCode,
        string correlationId) => new()
    {
        Id = Guid.NewGuid(),
        LegalEntityId = legalEntityId,
        SiteCode = siteCode,
        PumpNumber = 1,
        NozzleNumber = 1,
        FccTransactionId = $"TX-{Guid.NewGuid():N}",
        ProductCode = "PMS",
        AmountMinorUnits = 5000,
        VolumeMicrolitres = 10_000_000,
        UnitPriceMinorPerLitre = 500,
        CurrencyCode = "MWK",
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        FccVendor = FccVendor.DOMS,
        Status = TransactionStatus.PENDING,
        IngestionSource = IngestionSource.FCC_PUSH,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        CorrelationId = Guid.NewGuid(),
        FccCorrelationId = correlationId,
        OdooOrderId = $"ODOO-{Guid.NewGuid():N}"
    };

    private static ReconciliationRecord CreateUnmatchedRecord(Transaction transaction) => new()
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
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        LastMatchAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
    };

    private static PreAuthRecord CreatePreAuth(
        Guid legalEntityId,
        string siteCode,
        string correlationId) => new()
    {
        Id = Guid.NewGuid(),
        LegalEntityId = legalEntityId,
        SiteCode = siteCode,
        OdooOrderId = $"ODOO-{Guid.NewGuid():N}",
        PumpNumber = 1,
        NozzleNumber = 1,
        ProductCode = "PMS",
        CurrencyCode = "MWK",
        RequestedAmountMinorUnits = 5000,
        AuthorizedAmountMinorUnits = 5000,
        Status = PreAuthStatus.AUTHORIZED,
        RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
        AuthorizedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20),
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        FccCorrelationId = correlationId
    };
}

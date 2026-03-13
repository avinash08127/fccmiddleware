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

namespace FccMiddleware.IntegrationTests.PreAuth;

public sealed class PreAuthExpiryWorkerTests
{
    [Fact]
    public async Task ExpireBatchAsync_ExpiresActiveRecords_AndWritesOutboxEvents()
    {
        var services = BuildServices();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

        var legalEntityId = Guid.NewGuid();
        var expiredPending = CreateRecord(legalEntityId, PreAuthStatus.PENDING, expiresInMinutes: -30);
        var expiredAuthorized = CreateRecord(legalEntityId, PreAuthStatus.AUTHORIZED, expiresInMinutes: -20);
        var expiredDispensing = CreateRecord(legalEntityId, PreAuthStatus.DISPENSING, expiresInMinutes: -10);
        var activePending = CreateRecord(legalEntityId, PreAuthStatus.PENDING, expiresInMinutes: 15);

        db.PreAuthRecords.AddRange(
            expiredPending,
            expiredAuthorized,
            expiredDispensing,
            activePending);
        await db.SaveChangesAsync();

        var worker = new PreAuthExpiryWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PreAuthExpiryWorker>.Instance,
            Options.Create(new PreAuthExpiryWorkerOptions { BatchSize = 10 }));

        var expired = await worker.ExpireBatchAsync(CancellationToken.None);

        expired.Should().Be(3);

        await db.Entry(expiredPending).ReloadAsync();
        await db.Entry(expiredAuthorized).ReloadAsync();
        await db.Entry(expiredDispensing).ReloadAsync();
        await db.Entry(activePending).ReloadAsync();

        expiredPending.Status.Should().Be(PreAuthStatus.EXPIRED);
        expiredAuthorized.Status.Should().Be(PreAuthStatus.EXPIRED);
        expiredDispensing.Status.Should().Be(PreAuthStatus.EXPIRED);
        activePending.Status.Should().Be(PreAuthStatus.PENDING);

        var outboxCount = await db.OutboxMessages.CountAsync(m => m.EventType == "PreAuthExpired");
        outboxCount.Should().Be(3);
    }

    [Fact]
    public async Task ExpireBatchAsync_DispensingRecord_ExpiresWithoutDirectFccDeauth()
    {
        // PA-S05: Cloud no longer calls FCC directly for deauthorization.
        // Deauth is delegated to the edge agent's local expiry mechanism.
        var services = BuildServices();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

        var record = CreateRecord(Guid.NewGuid(), PreAuthStatus.DISPENSING, expiresInMinutes: -5);
        db.PreAuthRecords.Add(record);
        await db.SaveChangesAsync();

        var worker = new PreAuthExpiryWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PreAuthExpiryWorker>.Instance,
            Options.Create(new PreAuthExpiryWorkerOptions { BatchSize = 10 }));

        var expired = await worker.ExpireBatchAsync(CancellationToken.None);

        expired.Should().Be(1);

        await db.Entry(record).ReloadAsync();
        record.Status.Should().Be(PreAuthStatus.EXPIRED);

        var outboxCount = await db.OutboxMessages.CountAsync(m => m.EventType == "PreAuthExpired");
        outboxCount.Should().Be(1);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();

        services.AddSingleton<ICurrentTenantProvider>(new TestTenantProvider());
        services.AddDbContext<FccMiddlewareDbContext>(opts =>
            opts.UseInMemoryDatabase(databaseName));
        services.AddScoped<IEventPublisher, OutboxEventPublisher>();
        services.AddLogging();

        return services.BuildServiceProvider();
    }

    private static PreAuthRecord CreateRecord(
        Guid legalEntityId,
        PreAuthStatus status,
        int expiresInMinutes) => new()
    {
        Id = Guid.NewGuid(),
        LegalEntityId = legalEntityId,
        SiteCode = "SITE-PREAUTH-001",
        OdooOrderId = $"ODOO-{Guid.NewGuid():N}",
        PumpNumber = 4,
        NozzleNumber = 2,
        ProductCode = "PMS",
        CurrencyCode = "GHS",
        RequestedAmountMinorUnits = 500_00,
        Status = status,
        RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-40),
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expiresInMinutes),
        AuthorizedAt = status is PreAuthStatus.AUTHORIZED or PreAuthStatus.DISPENSING ? DateTimeOffset.UtcNow.AddMinutes(-35) : null,
        DispensingAt = status == PreAuthStatus.DISPENSING ? DateTimeOffset.UtcNow.AddMinutes(-15) : null,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-40),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-40)
    };

    private sealed class TestTenantProvider : ICurrentTenantProvider
    {
        public Guid? CurrentLegalEntityId => null;
    }

}

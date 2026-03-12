using FccMiddleware.Application.Ingestion;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Models.Adapter;
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
        var services = BuildServices(new NoOpFccAdapterFactory(), new StaticSiteFccConfigProvider());

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
    public async Task ExpireBatchAsync_DispensingRecord_AttemptsPumpDeauthorizationWhenAdapterSupportsIt()
    {
        var deauthAdapter = new FakePumpDeauthAdapter();
        var services = BuildServices(
            new SingleAdapterFactory(deauthAdapter),
            new StaticSiteFccConfigProvider());

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
        deauthAdapter.CallCount.Should().Be(1);
        deauthAdapter.LastPumpNumber.Should().Be(record.PumpNumber);
        deauthAdapter.LastNozzleNumber.Should().Be(record.NozzleNumber);
    }

    private static ServiceProvider BuildServices(
        IFccAdapterFactory adapterFactory,
        ISiteFccConfigProvider siteFccConfigProvider)
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();

        services.AddSingleton<ICurrentTenantProvider>(new TestTenantProvider());
        services.AddDbContext<FccMiddlewareDbContext>(opts =>
            opts.UseInMemoryDatabase(databaseName));
        services.AddScoped<IEventPublisher, OutboxEventPublisher>();
        services.AddSingleton<IFccAdapterFactory>(adapterFactory);
        services.AddSingleton<ISiteFccConfigProvider>(siteFccConfigProvider);
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

    private sealed class StaticSiteFccConfigProvider : ISiteFccConfigProvider
    {
        public Task<(SiteFccConfig Config, Guid LegalEntityId)?> GetBySiteCodeAsync(string siteCode, CancellationToken ct = default)
        {
            var legalEntityId = Guid.Parse("10000000-0000-0000-0000-000000000001");
            return Task.FromResult<(SiteFccConfig Config, Guid LegalEntityId)?>((
                new SiteFccConfig
                {
                    SiteCode = siteCode,
                    FccVendor = FccVendor.DOMS,
                    ConnectionProtocol = ConnectionProtocol.REST,
                    HostAddress = "127.0.0.1",
                    Port = 8080,
                    ApiKey = string.Empty,
                    IngestionMethod = IngestionMethod.PUSH,
                    CurrencyCode = "GHS",
                    Timezone = "Africa/Accra"
                },
                legalEntityId));
        }

        public Task<(SiteFccConfig Config, Guid LegalEntityId)?> GetByUsnCodeAsync(int usnCode, CancellationToken ct = default)
            => Task.FromResult<(SiteFccConfig Config, Guid LegalEntityId)?>(null);

        public Task<(SiteFccConfig Config, Guid LegalEntityId)?> GetByWebhookSecretAsync(string webhookSecret, CancellationToken ct = default)
            => Task.FromResult<(SiteFccConfig Config, Guid LegalEntityId)?>(null);

        public Task<(SiteFccConfig Config, Guid LegalEntityId)?> GetByAdvatecWebhookTokenAsync(string webhookToken, CancellationToken ct = default)
            => Task.FromResult<(SiteFccConfig Config, Guid LegalEntityId)?>(null);
    }

    private sealed class NoOpFccAdapterFactory : IFccAdapterFactory
    {
        public IFccAdapter Resolve(FccVendor vendor, SiteFccConfig config) => new NoOpAdapter();
    }

    private sealed class SingleAdapterFactory : IFccAdapterFactory
    {
        private readonly IFccAdapter _adapter;

        public SingleAdapterFactory(IFccAdapter adapter)
        {
            _adapter = adapter;
        }

        public IFccAdapter Resolve(FccVendor vendor, SiteFccConfig config) => _adapter;
    }

    private class NoOpAdapter : IFccAdapter
    {
        public CanonicalTransaction NormalizeTransaction(RawPayloadEnvelope rawPayload) => throw new NotSupportedException();
        public ValidationResult ValidatePayload(RawPayloadEnvelope rawPayload) => ValidationResult.Ok();
        public Task<TransactionBatch> FetchTransactionsAsync(FetchCursor cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public AdapterInfo GetAdapterMetadata() => new()
        {
            Vendor = FccVendor.DOMS,
            AdapterVersion = "test",
            SupportedIngestionMethods = [IngestionMethod.PUSH],
            SupportsPreAuth = false,
            SupportsPumpStatus = false,
            Protocol = "REST"
        };
    }

    private sealed class FakePumpDeauthAdapter : NoOpAdapter, IFccPumpDeauthorizationAdapter
    {
        public int CallCount { get; private set; }
        public int? LastPumpNumber { get; private set; }
        public int? LastNozzleNumber { get; private set; }

        public Task DeauthorizePumpAsync(int pumpNumber, int? nozzleNumber, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPumpNumber = pumpNumber;
            LastNozzleNumber = nozzleNumber;
            return Task.CompletedTask;
        }
    }
}

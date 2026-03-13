using FccMiddleware.Application.AgentConfig;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace FccMiddleware.UnitTests.AgentConfig;

public sealed class GetAgentConfigHandlerTests
{
    [Fact]
    public async Task Handle_SkipsOrphanedNozzles_WhenProductNavigationIsNull()
    {
        var legalEntityId = Guid.Parse("77000000-0000-0000-0000-000000000001");
        var siteId = Guid.Parse("77000000-0000-0000-0000-000000000002");
        var deviceId = Guid.Parse("77000000-0000-0000-0000-000000000003");
        var productId = Guid.Parse("77000000-0000-0000-0000-000000000004");
        var pumpId = Guid.Parse("77000000-0000-0000-0000-000000000005");
        var now = DateTimeOffset.Parse("2026-03-13T00:00:00Z");

        var legalEntity = new LegalEntity
        {
            Id = legalEntityId,
            CountryCode = "ZW",
            Name = "Config Test LE",
            CurrencyCode = "ZWL",
            TaxAuthorityCode = "ZIMRA",
            DefaultTimezone = "Africa/Harare",
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        var product = new Product
        {
            Id = productId,
            LegalEntityId = legalEntityId,
            ProductCode = "PMS",
            ProductName = "Premium Motor Spirit",
            IsActive = true,
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        var pump = new Pump
        {
            Id = pumpId,
            SiteId = siteId,
            LegalEntityId = legalEntityId,
            PumpNumber = 1,
            FccPumpNumber = 101,
            IsActive = true,
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        var validNozzle = new Nozzle
        {
            Id = Guid.Parse("77000000-0000-0000-0000-000000000006"),
            PumpId = pumpId,
            SiteId = siteId,
            LegalEntityId = legalEntityId,
            OdooNozzleNumber = 1,
            FccNozzleNumber = 201,
            ProductId = productId,
            Product = product,
            IsActive = true,
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        var orphanedNozzle = new Nozzle
        {
            Id = Guid.Parse("77000000-0000-0000-0000-000000000007"),
            PumpId = pumpId,
            SiteId = siteId,
            LegalEntityId = legalEntityId,
            OdooNozzleNumber = 2,
            FccNozzleNumber = 202,
            ProductId = Guid.Parse("77000000-0000-0000-0000-000000000008"),
            Product = null!,
            IsActive = true,
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        pump.Nozzles = [validNozzle, orphanedNozzle];

        var site = new Site
        {
            Id = siteId,
            LegalEntityId = legalEntityId,
            SiteCode = "CFG-SITE-UT-001",
            SiteName = "Unit Test Site",
            OperatingModel = SiteOperatingModel.COCO,
            SiteUsesPreAuth = true,
            ConnectivityMode = "CONNECTED",
            CompanyTaxPayerId = "TIN-001",
            FiscalizationMode = FiscalizationMode.NONE,
            IsActive = true,
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Pumps = [pump]
        };

        var fccConfig = new FccConfig
        {
            Id = Guid.Parse("77000000-0000-0000-0000-000000000009"),
            SiteId = siteId,
            LegalEntityId = legalEntityId,
            Site = site,
            LegalEntity = legalEntity,
            FccVendor = FccVendor.DOMS,
            ConnectionProtocol = ConnectionProtocol.REST,
            HostAddress = "192.168.1.100",
            Port = 9090,
            CredentialRef = "test-cred",
            IngestionMethod = IngestionMethod.PUSH,
            IngestionMode = IngestionMode.CLOUD_DIRECT,
            HeartbeatIntervalSeconds = 30,
            IsActive = true,
            ConfigVersion = 7,
            CreatedAt = now,
            UpdatedAt = now
        };

        var agent = new AgentRegistration
        {
            Id = deviceId,
            SiteId = siteId,
            LegalEntityId = legalEntityId,
            SiteCode = site.SiteCode,
            DeviceSerialNumber = "SN-UT-001",
            DeviceModel = "Urovo",
            OsVersion = "Android 12",
            AgentVersion = "1.0.0",
            IsActive = true,
            TokenHash = "hash",
            TokenExpiresAt = now.AddDays(1),
            RegisteredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EdgeAgentDefaults:Sync:CloudBaseUrl"] = "https://test.fccmiddleware.com"
            })
            .Build();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new FakeAgentConfigDbContext(agent, fccConfig);
        var sut = new GetAgentConfigHandler(db, configuration, cache);

        var result = await sut.Handle(new GetAgentConfigQuery
        {
            DeviceId = deviceId,
            SiteCode = site.SiteCode,
            LegalEntityId = legalEntityId
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Config.Should().NotBeNull();
        result.Value.Config!.Mappings.Products.Should().ContainSingle();
        result.Value.Config.Mappings.Nozzles.Should().ContainSingle();
        result.Value.Config.Mappings.Nozzles[0].ProductCode.Should().Be("PMS");
    }

    private sealed class FakeAgentConfigDbContext : IAgentConfigDbContext
    {
        private readonly AgentRegistration? _agent;
        private readonly FccConfig? _fccConfig;

        public FakeAgentConfigDbContext(AgentRegistration? agent, FccConfig? fccConfig)
        {
            _agent = agent;
            _fccConfig = fccConfig;
        }

        public Task<FccConfig?> GetFccConfigWithSiteDataAsync(string siteCode, Guid legalEntityId, CancellationToken ct) =>
            Task.FromResult(_fccConfig);

        public Task<AgentRegistration?> FindAgentByDeviceIdAsync(Guid deviceId, CancellationToken ct) =>
            Task.FromResult(_agent);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FccMiddleware.IntegrationTests.MasterData;

/// <summary>
/// Integration tests for the master data sync endpoints (PUT /api/v1/master-data/*).
/// Verifies Databricks API key auth, upsert (insert + update), opt-in soft-delete for full snapshots,
/// syncedAt timestamp updates, and outbox event publishing.
/// </summary>
[Collection("Integration")]
public sealed class MasterDataSyncTests : IAsyncLifetime
{
    private const string TestRawApiKey   = "test-databricks-api-key-sync-32chars-xx";
    private const string WrongRoleApiKey = "test-wrong-role-api-key-sync-32chars-xx";

    // Use unique country codes to avoid collisions with seed data (MW, TZ, BW, ZM, NA).
    private static readonly Guid TestLegalEntityId = Guid.Parse("88000000-0000-0000-0000-000000000001");
    private static readonly Guid SiteId1           = Guid.Parse("88000000-0000-0000-0000-000000000010");
    private static readonly Guid ProductId1        = Guid.Parse("88000000-0000-0000-0000-000000000020");
    private static readonly Guid PumpId1           = Guid.Parse("88000000-0000-0000-0000-000000000030");
    private static readonly Guid OperatorId1       = Guid.Parse("88000000-0000-0000-0000-000000000040");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine").Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:FccMiddleware"] = _postgres.GetConnectionString(),
                        ["ConnectionStrings:Redis"]         = _redis.GetConnectionString()
                    });
                });
            });

        _ = _factory.Server;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        await db.Database.EnsureCreatedAsync();
        await SeedTestDataAsync(db);

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", TestRawApiKey);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.StopAsync(), _redis.StopAsync());
    }

    // ── Legal entity sync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SyncLegalEntities_InsertsNewRecord_AndReturnsUpsertedCount()
    {
        var newId = Guid.NewGuid();
        var body = new
        {
            legalEntities = new[]
            {
                new
                {
                    id = newId,
                    code = "KE",
                    name = "Kenya Ltd",
                    currencyCode = "KES",
                    taxAuthorityCode = "KRA",
                    defaultFiscalizationMode = "EXTERNAL_INTEGRATION",
                    fiscalizationProvider = "ETIMS",
                    defaultTimezone = "Africa/Nairobi",
                    isActive = true
                },
                new
                {
                    id = TestLegalEntityId,
                    code = "CD",
                    name = "Congo Ltd",
                    currencyCode = "CDF",
                    taxAuthorityCode = "DGI",
                    defaultFiscalizationMode = "NONE",
                    defaultTimezone = "Africa/Kinshasa",
                    isActive = true
                }
            }
        };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/legal-entities", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("upsertedCount").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        json.GetProperty("errorCount").GetInt32().Should().Be(0);

        // Verify DB state
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var entity = await db.LegalEntities.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == newId);
        entity.Should().NotBeNull();
        entity!.CountryCode.Should().Be("KE");
        entity.Name.Should().Be("Kenya Ltd");
        entity.TaxAuthorityCode.Should().Be("KRA");
        entity.DefaultFiscalizationMode.Should().Be(FiscalizationMode.EXTERNAL_INTEGRATION);
        entity.FiscalizationProvider.Should().Be("ETIMS");
        entity.DefaultTimezone.Should().Be("Africa/Nairobi");
        entity.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SyncLegalEntities_UpdatesExistingRecord_OnFieldChange()
    {
        // TestLegalEntityId already exists with name "Congo Ltd (seed)"
        var body = new
        {
            legalEntities = new[]
            {
                new
                {
                    id = TestLegalEntityId,
                    code = "CD",
                    name = "Congo Ltd (updated)",
                    currencyCode = "CDF",
                    taxAuthorityCode = "DGI-UPD",
                    defaultFiscalizationMode = "FCC_DIRECT",
                    defaultTimezone = "Africa/Lubumbashi",
                    isActive = true
                }
            }
        };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/legal-entities", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("upsertedCount").GetInt32().Should().Be(1);
        json.GetProperty("unchangedCount").GetInt32().Should().Be(0);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var entity = await db.LegalEntities.IgnoreQueryFilters().FirstAsync(e => e.Id == TestLegalEntityId);
        entity.Name.Should().Be("Congo Ltd (updated)");
        entity.TaxAuthorityCode.Should().Be("DGI-UPD");
        entity.DefaultFiscalizationMode.Should().Be(FiscalizationMode.FCC_DIRECT);
        entity.DefaultTimezone.Should().Be("Africa/Lubumbashi");
    }

    [Fact]
    public async Task SyncLegalEntities_PartialBatch_DoesNotDeactivateAbsentRecords_ByDefault()
    {
        var extraId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            db.LegalEntities.Add(MakeLegalEntity(extraId, "GW"));
            await db.SaveChangesAsync();
        }

        var body = new
        {
            legalEntities = new[]
            {
                new
                {
                    id = TestLegalEntityId,
                    code = "CD",
                    name = "Congo Ltd",
                    currencyCode = "CDF",
                    taxAuthorityCode = "DGI",
                    defaultFiscalizationMode = "NONE",
                    defaultTimezone = "Africa/Kinshasa",
                    isActive = true
                }
            }
        };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/legal-entities", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("deactivatedCount").GetInt32().Should().Be(0);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var extra = await verifyDb.LegalEntities.IgnoreQueryFilters().FirstAsync(e => e.Id == extraId);
        extra.IsActive.Should().BeTrue();
        extra.DeactivatedAt.Should().BeNull();
    }

    [Fact]
    public async Task SyncLegalEntities_FullSnapshot_SoftDeletesAbsentRecords()
    {
        // Extra entity to be deactivated
        var extraId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            db.LegalEntities.Add(MakeLegalEntity(extraId, "GW"));
            await db.SaveChangesAsync();
        }

        // Sync with only TestLegalEntityId — extraId should be deactivated
        var body = new
        {
            isFullSnapshot = true,
            legalEntities = new[]
            {
                new
                {
                    id = TestLegalEntityId,
                    code = "CD",
                    name = "Congo Ltd",
                    currencyCode = "CDF",
                    taxAuthorityCode = "DGI",
                    defaultFiscalizationMode = "NONE",
                    defaultTimezone = "Africa/Kinshasa",
                    isActive = true
                }
            }
        };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/legal-entities", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("deactivatedCount").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var extra = await verifyDb.LegalEntities.IgnoreQueryFilters().FirstAsync(e => e.Id == extraId);
        extra.IsActive.Should().BeFalse();
        extra.DeactivatedAt.Should().NotBeNull();
    }

    // ── Site sync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncSites_InsertsNewSite_AndSyncedAtIsSet()
    {
        var newSiteId = Guid.NewGuid();
        var body = new
        {
            sites = new[]
            {
                new
                {
                    id = newSiteId, siteCode = $"SYNC-SITE-{newSiteId:N}".Substring(0, 20),
                    legalEntityId = TestLegalEntityId, siteName = "Test Sync Site",
                    operatingModel = "COCO",
                    connectivityMode = "DISCONNECTED",
                    companyTaxPayerId = "TIN-SYNC-NEW",
                    fiscalizationMode = "EXTERNAL_INTEGRATION",
                    taxAuthorityEndpoint = "https://tax.new.example.test",
                    requireCustomerTaxId = true,
                    fiscalReceiptRequired = false,
                    odooSiteId = "ODOO-SYNC-NEW",
                    isActive = true
                },
                new
                {
                    id = SiteId1, siteCode = "SYNC-SITE-001",
                    legalEntityId = TestLegalEntityId, siteName = "Existing Site",
                    operatingModel = "COCO",
                    connectivityMode = "CONNECTED",
                    companyTaxPayerId = "TIN-SYNC-001",
                    fiscalizationMode = "FCC_DIRECT",
                    requireCustomerTaxId = true,
                    fiscalReceiptRequired = true,
                    odooSiteId = "ODOO-SYNC-001",
                    isActive = true
                }
            }
        };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/sites", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCount").GetInt32().Should().Be(0);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var site = await db.Sites.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == newSiteId);
        site.Should().NotBeNull();
        site!.SiteName.Should().Be("Test Sync Site");
        site.ConnectivityMode.Should().Be("DISCONNECTED");
        site.CompanyTaxPayerId.Should().Be("TIN-SYNC-NEW");
        site.FiscalizationMode.Should().Be(FiscalizationMode.EXTERNAL_INTEGRATION);
        site.TaxAuthorityEndpoint.Should().Be("https://tax.new.example.test");
        site.RequireCustomerTaxId.Should().BeTrue();
        site.OdooSiteId.Should().Be("ODOO-SYNC-NEW");
        site.SyncedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SyncSites_PartialBatch_DoesNotDeactivateAbsentRecords_ByDefault()
    {
        var extraSiteId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            db.Sites.Add(new Site
            {
                Id               = extraSiteId,
                LegalEntityId    = TestLegalEntityId,
                SiteCode         = "SYNC-SITE-EXTRA",
                SiteName         = "Extra Sync Site",
                OperatingModel   = Domain.Enums.SiteOperatingModel.COCO,
                ConnectivityMode = "CONNECTED",
                CompanyTaxPayerId = "TIN-SYNC-EXTRA",
                IsActive         = true,
                SyncedAt         = DateTimeOffset.UtcNow.AddDays(-1),
                CreatedAt        = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt        = DateTimeOffset.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        var body = new
        {
            sites = new[]
            {
                new
                {
                    id = SiteId1, siteCode = "SYNC-SITE-001",
                    legalEntityId = TestLegalEntityId, siteName = "Existing Site",
                    operatingModel = "COCO",
                    connectivityMode = "CONNECTED",
                    companyTaxPayerId = "TIN-SYNC-001",
                    fiscalizationMode = "FCC_DIRECT",
                    requireCustomerTaxId = true,
                    fiscalReceiptRequired = true,
                    odooSiteId = "ODOO-SYNC-001",
                    isActive = true
                }
            }
        };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/sites", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("deactivatedCount").GetInt32().Should().Be(0);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var extraSite = await verifyDb.Sites.IgnoreQueryFilters().FirstAsync(s => s.Id == extraSiteId);
        extraSite.IsActive.Should().BeTrue();
        extraSite.DeactivatedAt.Should().BeNull();
    }

    // ── Product sync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncProducts_InsertsNewProduct_UpdatesExisting()
    {
        var newProductId = Guid.NewGuid();
        var body = new
        {
            products = new[]
            {
                new { id = newProductId, legalEntityId = TestLegalEntityId, canonicalCode = "LPG",    displayName = "Liquefied Petroleum Gas", isActive = true },
                new { id = ProductId1,   legalEntityId = TestLegalEntityId, canonicalCode = "PMS",    displayName = "Unleaded Petrol (updated)", isActive = true }
            }
        };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/products", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCount").GetInt32().Should().Be(0);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

        var newProduct = await db.Products.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == newProductId);
        newProduct.Should().NotBeNull();
        newProduct!.ProductCode.Should().Be("LPG");

        var updated = await db.Products.IgnoreQueryFilters().FirstAsync(p => p.Id == ProductId1);
        updated.ProductName.Should().Be("Unleaded Petrol (updated)");
    }

    [Fact]
    public async Task SyncProducts_PartialBatch_DoesNotDeactivateAbsentRecords_ByDefault()
    {
        var extraProductId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            db.Products.Add(new Product
            {
                Id            = extraProductId,
                LegalEntityId = TestLegalEntityId,
                ProductCode   = "AGO",
                ProductName   = "Diesel",
                UnitOfMeasure = "LITRE",
                IsActive      = true,
                SyncedAt      = DateTimeOffset.UtcNow.AddDays(-1),
                CreatedAt     = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt     = DateTimeOffset.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        var body = new
        {
            products = new[]
            {
                new { id = ProductId1, legalEntityId = TestLegalEntityId, canonicalCode = "PMS", displayName = "Unleaded Petrol", isActive = true }
            }
        };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/products", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("deactivatedCount").GetInt32().Should().Be(0);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var extraProduct = await verifyDb.Products.IgnoreQueryFilters().FirstAsync(p => p.Id == extraProductId);
        extraProduct.IsActive.Should().BeTrue();
    }

    // ── Operator sync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncOperators_PartialBatch_DoesNotDeactivateAbsentRecords_ByDefault()
    {
        var extraOperatorId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            db.Operators.Add(new Operator
            {
                Id            = extraOperatorId,
                LegalEntityId = TestLegalEntityId,
                OperatorCode  = extraOperatorId.ToString("N")[..16],
                OperatorName  = "Extra Dealer",
                IsActive      = true,
                SyncedAt      = DateTimeOffset.UtcNow.AddDays(-1),
                CreatedAt     = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt     = DateTimeOffset.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        var body = new
        {
            operators = new[]
            {
                new { id = OperatorId1, legalEntityId = TestLegalEntityId, name = "Dealer One", isActive = true }
            }
        };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/operators", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("deactivatedCount").GetInt32().Should().Be(0);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var extraOperator = await verifyDb.Operators.IgnoreQueryFilters().FirstAsync(o => o.Id == extraOperatorId);
        extraOperator.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SyncOperators_FullSnapshot_InsertsUpdatesDeactivates()
    {
        // Insert a new operator that will be deactivated in the second sync
        var ephemeralId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            db.Operators.Add(new Operator
            {
                Id            = ephemeralId,
                LegalEntityId = TestLegalEntityId,
                OperatorCode  = ephemeralId.ToString("N")[..16],
                OperatorName  = "Soon-Deactivated Dealer",
                IsActive      = true,
                SyncedAt      = DateTimeOffset.UtcNow.AddDays(-1),
                CreatedAt     = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt     = DateTimeOffset.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        // Sync: only OperatorId1 — ephemeralId gets soft-deleted
        var body = new
        {
            isFullSnapshot = true,
            operators = new[]
            {
                new { id = OperatorId1, legalEntityId = TestLegalEntityId, name = "Dealer One (updated)", isActive = true }
            }
        };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/operators", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("deactivatedCount").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

        var ephemeral = await verifyDb.Operators.IgnoreQueryFilters().FirstAsync(o => o.Id == ephemeralId);
        ephemeral.IsActive.Should().BeFalse();

        var updated = await verifyDb.Operators.IgnoreQueryFilters().FirstAsync(o => o.Id == OperatorId1);
        updated.OperatorName.Should().Be("Dealer One (updated)");
    }

    // ── Auth tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncLegalEntities_WithoutApiKey_Returns401()
    {
        var client = _factory.CreateClient(); // no X-Api-Key header
        var body = new
        {
            legalEntities = new[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    code = "NG",
                    name = "Nigeria",
                    currencyCode = "NGN",
                    taxAuthorityCode = "FIRS",
                    defaultFiscalizationMode = "EXTERNAL_INTEGRATION",
                    defaultTimezone = "Africa/Lagos",
                    isActive = true
                }
            }
        };

        var response = await client.PutAsJsonAsync("/api/v1/master-data/legal-entities", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SyncLegalEntities_WrongRoleKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", WrongRoleApiKey);

        var body = new
        {
            legalEntities = new[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    code = "NG",
                    name = "Nigeria",
                    currencyCode = "NGN",
                    taxAuthorityCode = "FIRS",
                    defaultFiscalizationMode = "EXTERNAL_INTEGRATION",
                    defaultTimezone = "Africa/Lagos",
                    isActive = true
                }
            }
        };

        var response = await client.PutAsJsonAsync("/api/v1/master-data/legal-entities", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SyncLegalEntities_EmptyBatch_Returns400()
    {
        var body = new { legalEntities = Array.Empty<object>() };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/legal-entities", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SyncPumps_WithOutboxEvent_OnChange()
    {
        var newPumpId = Guid.NewGuid();
        var body = new
        {
            pumps = new[]
            {
                new
                {
                    id         = newPumpId,
                    siteCode   = "SYNC-SITE-001",
                    pumpNumber = 1,
                    fccPumpNumber = 11,
                    nozzles    = new[]
                    {
                        new { nozzleNumber = 1, fccNozzleNumber = 101, canonicalProductCode = "PMS" }
                    },
                    isActive   = true
                },
                new
                {
                    id         = PumpId1,
                    siteCode   = "SYNC-SITE-001",
                    pumpNumber = 2,
                    fccPumpNumber = 22,
                    nozzles    = new[]
                    {
                        new { nozzleNumber = 1, fccNozzleNumber = 202, canonicalProductCode = "PMS" }
                    },
                    isActive   = true
                }
            }
        };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/pumps", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCount").GetInt32().Should().Be(0);

        // Verify outbox event
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var outbox = await db.OutboxMessages.Where(m => m.EventType == "PumpsSynced").ToListAsync();
        outbox.Should().NotBeEmpty();

        // Verify nozzle was created
        var nozzle = await db.Nozzles.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.PumpId == newPumpId);
        nozzle.Should().NotBeNull();
        nozzle!.OdooNozzleNumber.Should().Be(1);
        nozzle.FccNozzleNumber.Should().Be(101);

        var newPump = await db.Pumps.IgnoreQueryFilters().FirstAsync(p => p.Id == newPumpId);
        newPump.FccPumpNumber.Should().Be(11);
    }

    [Fact]
    public async Task SyncPumps_PartialBatch_DoesNotDeactivateAbsentRecords_ByDefault()
    {
        var extraPumpId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            db.Pumps.Add(new Pump
            {
                Id            = extraPumpId,
                SiteId        = SiteId1,
                LegalEntityId = TestLegalEntityId,
                PumpNumber    = 99,
                FccPumpNumber = 99,
                IsActive      = true,
                SyncedAt      = DateTimeOffset.UtcNow.AddDays(-1),
                CreatedAt     = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt     = DateTimeOffset.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        var body = new
        {
            pumps = new[]
            {
                new
                {
                    id         = PumpId1,
                    siteCode   = "SYNC-SITE-001",
                    pumpNumber = 2,
                    fccPumpNumber = 22,
                    nozzles    = new[] { new { nozzleNumber = 1, fccNozzleNumber = 202, canonicalProductCode = "PMS" } },
                    isActive   = true
                }
            }
        };

        var response = await _client.PutAsJsonAsync("/api/v1/master-data/pumps", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("deactivatedCount").GetInt32().Should().Be(0);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var extraPump = await verifyDb.Pumps.IgnoreQueryFilters().FirstAsync(p => p.Id == extraPumpId);
        extraPump.IsActive.Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ComputeSha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static LegalEntity MakeLegalEntity(Guid id, string countryCode) => new()
    {
        Id                    = id,
        CountryCode           = countryCode,
        Name                  = $"{countryCode} Test Entity",
        CurrencyCode          = "USD",
        TaxAuthorityCode      = "TEST",
        FiscalizationRequired = false,
        DefaultTimezone       = "UTC",
        IsActive              = true,
        SyncedAt              = DateTimeOffset.UtcNow,
        CreatedAt             = DateTimeOffset.UtcNow,
        UpdatedAt             = DateTimeOffset.UtcNow
    };

    private static async Task SeedTestDataAsync(FccMiddlewareDbContext db)
    {
        if (await db.LegalEntities.IgnoreQueryFilters().AnyAsync(e => e.Id == TestLegalEntityId)) return;

        // ── Legal entity ──────────────────────────────────────────────────────
        db.LegalEntities.Add(new LegalEntity
        {
            Id                    = TestLegalEntityId,
            CountryCode           = "CD",
            Name                  = "Congo Ltd (seed)",
            CurrencyCode          = "CDF",
            TaxAuthorityCode      = "DGI",
            FiscalizationRequired = false,
            DefaultTimezone       = "Africa/Kinshasa",
            IsActive              = true,
            SyncedAt              = DateTimeOffset.UtcNow,
            CreatedAt             = DateTimeOffset.UtcNow,
            UpdatedAt             = DateTimeOffset.UtcNow
        });

        // ── Databricks API keys ───────────────────────────────────────────────
        db.DatabricksApiKeys.Add(new DatabricksApiKey
        {
            Id        = Guid.NewGuid(),
            KeyHash   = ComputeSha256Hex(TestRawApiKey),
            Label     = "Test Databricks Key",
            Role      = "master-data-sync",
            IsActive  = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Wrong-role key (should be rejected by the policy).
        db.DatabricksApiKeys.Add(new DatabricksApiKey
        {
            Id        = Guid.NewGuid(),
            KeyHash   = ComputeSha256Hex(WrongRoleApiKey),
            Label     = "Wrong Role Key",
            Role      = "read-only",       // intentionally wrong role
            IsActive  = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // ── Site ──────────────────────────────────────────────────────────────
        db.Sites.Add(new Site
        {
            Id               = SiteId1,
            LegalEntityId    = TestLegalEntityId,
            SiteCode         = "SYNC-SITE-001",
            SiteName         = "Sync Test Site 001",
            OperatingModel   = Domain.Enums.SiteOperatingModel.COCO,
            ConnectivityMode = "CONNECTED",
            CompanyTaxPayerId = "TIN-SYNC-SEED",
            IsActive         = true,
            SyncedAt         = DateTimeOffset.UtcNow,
            CreatedAt        = DateTimeOffset.UtcNow,
            UpdatedAt        = DateTimeOffset.UtcNow
        });

        // ── Product ───────────────────────────────────────────────────────────
        db.Products.Add(new Product
        {
            Id            = ProductId1,
            LegalEntityId = TestLegalEntityId,
            ProductCode   = "PMS",
            ProductName   = "Unleaded Petrol",
            UnitOfMeasure = "LITRE",
            IsActive      = true,
            SyncedAt      = DateTimeOffset.UtcNow,
            CreatedAt     = DateTimeOffset.UtcNow,
            UpdatedAt     = DateTimeOffset.UtcNow
        });

        // ── Pump ──────────────────────────────────────────────────────────────
        db.Pumps.Add(new Pump
        {
            Id            = PumpId1,
            SiteId        = SiteId1,
            LegalEntityId = TestLegalEntityId,
            PumpNumber    = 2,
            FccPumpNumber = 2,
            IsActive      = true,
            SyncedAt      = DateTimeOffset.UtcNow,
            CreatedAt     = DateTimeOffset.UtcNow,
            UpdatedAt     = DateTimeOffset.UtcNow
        });

        // ── Operator ──────────────────────────────────────────────────────────
        db.Operators.Add(new Operator
        {
            Id            = OperatorId1,
            LegalEntityId = TestLegalEntityId,
            OperatorCode  = "DEALER-001",
            OperatorName  = "Dealer One",
            IsActive      = true,
            SyncedAt      = DateTimeOffset.UtcNow,
            CreatedAt     = DateTimeOffset.UtcNow,
            UpdatedAt     = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }
}

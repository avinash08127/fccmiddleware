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

namespace FccMiddleware.IntegrationTests.Transactions;

/// <summary>
/// Integration tests for GET /api/v1/transactions (Odoo poll endpoint).
/// Verifies API key authentication, tenant scoping, filtering, pagination,
/// and that DUPLICATE/ARCHIVED transactions are never returned.
/// </summary>
[Collection("Integration")]
public sealed class PollTransactionsTests : IAsyncLifetime
{
    private const string TestRawApiKey   = "test-odoo-api-key-poll-integration-32x";
    private const string OtherRawApiKey  = "other-odoo-api-key-different-tenant-32";
    private const string TestSiteCode    = "POLL-SITE-001";
    private const string OtherSiteCode   = "POLL-SITE-002";

    private static readonly Guid TestLegalEntityId  = Guid.Parse("99000000-0000-0000-0000-000000000021");
    private static readonly Guid OtherLegalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000022");
    private static readonly Guid TestSiteId         = Guid.Parse("99000000-0000-0000-0000-000000000023");

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
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.StopAsync(), _redis.StopAsync());
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Poll_ValidApiKey_ReturnsPendingTransactions()
    {
        _client.DefaultRequestHeaders.Remove("X-Api-Key");
        _client.DefaultRequestHeaders.Add("X-Api-Key", TestRawApiKey);

        var response = await _client.GetAsync("/api/v1/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").EnumerateArray().ToList();
        var meta = body.GetProperty("meta");

        // 3 PENDING transactions were seeded for TestLegalEntityId
        data.Should().HaveCount(3);
        data.All(t => t.GetProperty("status").GetString() == "PENDING").Should().BeTrue();
        data.All(t => t.GetProperty("siteCode").GetString() == TestSiteCode).Should().BeTrue();

        meta.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        meta.GetProperty("pageSize").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Poll_WithoutApiKey_Returns401()
    {
        _client.DefaultRequestHeaders.Remove("X-Api-Key");

        var response = await _client.GetAsync("/api/v1/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Poll_InvalidApiKey_Returns401()
    {
        _client.DefaultRequestHeaders.Remove("X-Api-Key");
        _client.DefaultRequestHeaders.Add("X-Api-Key", "definitely-not-a-valid-key");

        var response = await _client.GetAsync("/api/v1/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Poll_DuplicateAndArchivedTransactions_NeverReturned()
    {
        _client.DefaultRequestHeaders.Remove("X-Api-Key");
        _client.DefaultRequestHeaders.Add("X-Api-Key", TestRawApiKey);

        var response = await _client.GetAsync("/api/v1/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").EnumerateArray().ToList();

        // DUPLICATE and ARCHIVED transactions seeded for this tenant should not appear
        var statuses = data.Select(t => t.GetProperty("status").GetString()).ToList();
        statuses.Should().NotContain("DUPLICATE");
        statuses.Should().NotContain("ARCHIVED");
    }

    [Fact]
    public async Task Poll_SiteCodeFilter_ReturnsOnlyMatchingSite()
    {
        // Seed a second site's transaction under the same legal entity
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        db.Transactions.Add(MakeTransaction(TestLegalEntityId, OtherSiteCode, "POLL-SITE2-001", 1));
        await db.SaveChangesAsync();

        _client.DefaultRequestHeaders.Remove("X-Api-Key");
        _client.DefaultRequestHeaders.Add("X-Api-Key", TestRawApiKey);

        var response = await _client.GetAsync($"/api/v1/transactions?siteCode={TestSiteCode}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").EnumerateArray().ToList();

        data.Should().NotBeEmpty();
        data.All(t => t.GetProperty("siteCode").GetString() == TestSiteCode).Should().BeTrue();
    }

    [Fact]
    public async Task Poll_Pagination_CursorAdvancesCorrectly()
    {
        _client.DefaultRequestHeaders.Remove("X-Api-Key");
        _client.DefaultRequestHeaders.Add("X-Api-Key", TestRawApiKey);

        // Page 1: request 2 of the 3 PENDING transactions
        var page1Response = await _client.GetAsync("/api/v1/transactions?pageSize=2");
        page1Response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page1Body = await page1Response.Content.ReadFromJsonAsync<JsonElement>();
        var page1Data = page1Body.GetProperty("data").EnumerateArray().ToList();
        var page1Meta = page1Body.GetProperty("meta");

        page1Data.Should().HaveCount(2);
        page1Meta.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        var nextCursor = page1Meta.GetProperty("nextCursor").GetString();
        nextCursor.Should().NotBeNullOrEmpty();

        // Page 2: use cursor to get remaining 1 transaction
        var page2Response = await _client.GetAsync($"/api/v1/transactions?pageSize=2&cursor={Uri.EscapeDataString(nextCursor!)}");
        page2Response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page2Body = await page2Response.Content.ReadFromJsonAsync<JsonElement>();
        var page2Data = page2Body.GetProperty("data").EnumerateArray().ToList();
        var page2Meta = page2Body.GetProperty("meta");

        page2Data.Should().HaveCount(1);
        page2Meta.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        page2Meta.TryGetProperty("nextCursor", out var nc).Should().BeTrue();
        (nc.ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(nc.GetString())).Should().BeTrue();

        // No overlap: page 2 should have a different fccTransactionId than either in page 1
        var page1Ids = page1Data.Select(t => t.GetProperty("fccTransactionId").GetString()).ToHashSet();
        var page2Ids = page2Data.Select(t => t.GetProperty("fccTransactionId").GetString()).ToList();
        page2Ids.Should().NotIntersectWith(page1Ids!);
    }

    [Fact]
    public async Task Poll_TenantIsolation_OtherTenantKeyReturnsOwnTransactionsOnly()
    {
        _client.DefaultRequestHeaders.Remove("X-Api-Key");
        _client.DefaultRequestHeaders.Add("X-Api-Key", OtherRawApiKey);

        var response = await _client.GetAsync("/api/v1/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").EnumerateArray().ToList();

        // Other tenant has 0 transactions seeded
        data.Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ComputeSha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Transaction MakeTransaction(
        Guid legalEntityId,
        string siteCode,
        string fccTransactionId,
        int pumpNumber,
        TransactionStatus status = TransactionStatus.PENDING)
    {
        var now = DateTimeOffset.UtcNow;
        return new Transaction
        {
            Id                     = Guid.NewGuid(),
            CreatedAt              = now,
            LegalEntityId          = legalEntityId,
            FccTransactionId       = fccTransactionId,
            SiteCode               = siteCode,
            PumpNumber             = pumpNumber,
            NozzleNumber           = 1,
            ProductCode            = "PMS",
            VolumeMicrolitres      = 30_000_000L,
            AmountMinorUnits       = 24_000_00L,
            UnitPriceMinorPerLitre = 800_00L,
            CurrencyCode           = "MWK",
            StartedAt              = now.AddMinutes(-10),
            CompletedAt            = now.AddMinutes(-5),
            FccVendor              = FccVendor.DOMS,
            Status                 = status,
            IngestionSource        = IngestionSource.FCC_PUSH,
            CorrelationId          = Guid.NewGuid(),
            UpdatedAt              = now
        };
    }

    private static async Task SeedTestDataAsync(FccMiddlewareDbContext db)
    {
        if (await db.LegalEntities.IgnoreQueryFilters().AnyAsync(e => e.Id == TestLegalEntityId)) return;

        // ── Legal entities ────────────────────────────────────────────────────
        db.LegalEntities.Add(new LegalEntity
        {
            Id                    = TestLegalEntityId,
            CountryCode           = "UG",  // Uganda — not in EnsureCreated seed data
            Name                  = "Poll Test Uganda Ltd",
            CurrencyCode          = "UGX",
            TaxAuthorityCode      = "URA",
            FiscalizationRequired = false,
            DefaultTimezone       = "Africa/Kampala",
            IsActive              = true,
            SyncedAt              = DateTimeOffset.UtcNow,
            CreatedAt             = DateTimeOffset.UtcNow,
            UpdatedAt             = DateTimeOffset.UtcNow
        });

        db.LegalEntities.Add(new LegalEntity
        {
            Id                    = OtherLegalEntityId,
            CountryCode           = "KE",  // Kenya — not in EnsureCreated seed data
            Name                  = "Poll Test Kenya Ltd",
            CurrencyCode          = "KES",
            TaxAuthorityCode      = "KRA",
            FiscalizationRequired = false,
            DefaultTimezone       = "Africa/Nairobi",
            IsActive              = true,
            SyncedAt              = DateTimeOffset.UtcNow,
            CreatedAt             = DateTimeOffset.UtcNow,
            UpdatedAt             = DateTimeOffset.UtcNow
        });

        // ── Site ──────────────────────────────────────────────────────────────
        db.Sites.Add(new Site
        {
            Id               = TestSiteId,
            LegalEntityId    = TestLegalEntityId,
            SiteCode         = TestSiteCode,
            SiteName         = "Poll Integration Test Station",
            OperatingModel   = SiteOperatingModel.COCO,
            CompanyTaxPayerId = "TAX-POLL-001",
            IsActive         = true,
            SyncedAt         = DateTimeOffset.UtcNow,
            CreatedAt        = DateTimeOffset.UtcNow,
            UpdatedAt        = DateTimeOffset.UtcNow
        });

        // ── Odoo API keys ─────────────────────────────────────────────────────
        db.OdooApiKeys.Add(new OdooApiKey
        {
            Id              = Guid.NewGuid(),
            LegalEntityId   = TestLegalEntityId,
            KeyHash         = ComputeSha256Hex(TestRawApiKey),
            Label           = "Poll Test Key MW",
            IsActive        = true,
            CreatedAt       = DateTimeOffset.UtcNow
        });

        db.OdooApiKeys.Add(new OdooApiKey
        {
            Id              = Guid.NewGuid(),
            LegalEntityId   = OtherLegalEntityId,
            KeyHash         = ComputeSha256Hex(OtherRawApiKey),
            Label           = "Poll Test Key TZ",
            IsActive        = true,
            CreatedAt       = DateTimeOffset.UtcNow
        });

        // ── Transactions: 3 PENDING + 1 DUPLICATE + 1 ARCHIVED for TestLegalEntityId ──
        db.Transactions.Add(MakeTransaction(TestLegalEntityId, TestSiteCode, "POLL-PENDING-001", 1));
        db.Transactions.Add(MakeTransaction(TestLegalEntityId, TestSiteCode, "POLL-PENDING-002", 2));
        db.Transactions.Add(MakeTransaction(TestLegalEntityId, TestSiteCode, "POLL-PENDING-003", 3));
        db.Transactions.Add(MakeTransaction(TestLegalEntityId, TestSiteCode, "POLL-DUPLICATE-001", 1, TransactionStatus.DUPLICATE));
        db.Transactions.Add(MakeTransaction(TestLegalEntityId, TestSiteCode, "POLL-ARCHIVED-001",  1, TransactionStatus.ARCHIVED));

        await db.SaveChangesAsync();
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FccMiddleware.IntegrationTests.Transactions;

/// <summary>
/// Integration tests for GET /api/v1/transactions/synced-status.
/// Verifies device JWT auth, site scoping, status filtering, and inclusive since filtering.
/// </summary>
[Collection("Integration")]
public sealed class GetSyncedStatusTests : IAsyncLifetime
{
    private const string TestSigningKey = "TestSigningKey-SyncedStatus-256bits-EdgeAgent!";
    private const string TestIssuer     = "fcc-middleware-cloud";
    private const string TestAudience   = "fcc-middleware-api";
    private const string TestSiteCode   = "SYNC-STATUS-SITE-001";
    private const string OtherSiteCode  = "SYNC-STATUS-SITE-002";
    private const string TestDeviceId   = "device-synced-status-test";

    private static readonly Guid TestLegalEntityId  = Guid.Parse("99000000-0000-0000-0000-000000000041");
    private static readonly Guid OtherLegalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000042");

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
                        ["ConnectionStrings:Redis"]         = _redis.GetConnectionString(),
                        ["DeviceJwt:SigningKey"]            = TestSigningKey,
                        ["DeviceJwt:Issuer"]                = TestIssuer,
                        ["DeviceJwt:Audience"]              = TestAudience
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

    [Fact]
    public async Task GetSyncedStatus_ReturnsOnlySyncedTransactions_ForJwtSite_SinceTimestamp()
    {
        var token = CreateDeviceJwt(TestDeviceId, TestSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var since = "2026-03-11T10:00:00Z";
        var response = await _client.GetAsync($"/api/v1/transactions/synced-status?since={Uri.EscapeDataString(since)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("fccTransactionIds")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToList();

        ids.Should().Equal("SYNCED-INCLUDED-001", "SYNCED-INCLUDED-002");
    }

    [Fact]
    public async Task GetSyncedStatus_SinceFilter_IsInclusive()
    {
        var token = CreateDeviceJwt(TestDeviceId, TestSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var since = "2026-03-11T11:00:00Z";
        var response = await _client.GetAsync($"/api/v1/transactions/synced-status?since={Uri.EscapeDataString(since)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("fccTransactionIds")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToList();

        ids.Should().ContainSingle().Which.Should().Be("SYNCED-INCLUDED-002");
    }

    [Fact]
    public async Task GetSyncedStatus_WithoutToken_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await _client.GetAsync("/api/v1/transactions/synced-status?since=2026-03-11T10:00:00Z");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetSyncedStatus_WithoutSince_Returns400()
    {
        var token = CreateDeviceJwt(TestDeviceId, TestSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.GetAsync("/api/v1/transactions/synced-status");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("VALIDATION.REQUIRED_SINCE");
    }

    private static string CreateDeviceJwt(
        string deviceId,
        string siteCode,
        Guid legalEntityId,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires = null)
    {
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var now = DateTimeOffset.UtcNow;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, deviceId),
            new Claim("site", siteCode),
            new Claim("lei", legalEntityId.ToString()),
            new Claim("roles", "edge-agent")
        };

        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: claims,
            notBefore: (notBefore ?? now).UtcDateTime,
            expires: (expires ?? now.AddHours(24)).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task SeedTestDataAsync(FccMiddlewareDbContext db)
    {
        if (await db.LegalEntities.IgnoreQueryFilters().AnyAsync(e => e.Id == TestLegalEntityId)) return;

        db.LegalEntities.Add(new LegalEntity
        {
            Id                    = TestLegalEntityId,
            CountryCode           = "GH",
            Name                  = "Synced Status Test Ghana Ltd",
            CurrencyCode          = "GHS",
            TaxAuthorityCode      = "GRA",
            FiscalizationRequired = false,
            DefaultTimezone       = "Africa/Accra",
            IsActive              = true,
            SyncedAt              = DateTimeOffset.UtcNow,
            CreatedAt             = DateTimeOffset.UtcNow,
            UpdatedAt             = DateTimeOffset.UtcNow
        });

        db.LegalEntities.Add(new LegalEntity
        {
            Id                    = OtherLegalEntityId,
            CountryCode           = "ZM",
            Name                  = "Other Tenant Zambia Ltd",
            CurrencyCode          = "ZMW",
            TaxAuthorityCode      = "ZRA",
            FiscalizationRequired = false,
            DefaultTimezone       = "Africa/Lusaka",
            IsActive              = true,
            SyncedAt              = DateTimeOffset.UtcNow,
            CreatedAt             = DateTimeOffset.UtcNow,
            UpdatedAt             = DateTimeOffset.UtcNow
        });

        db.Transactions.AddRange(
            MakeTransaction(
                TestLegalEntityId,
                TestSiteCode,
                "SYNCED-INCLUDED-001",
                TransactionStatus.SYNCED_TO_ODOO,
                syncedToOdooAt: DateTimeOffset.Parse("2026-03-11T10:00:00Z")),
            MakeTransaction(
                TestLegalEntityId,
                TestSiteCode,
                "SYNCED-INCLUDED-002",
                TransactionStatus.SYNCED_TO_ODOO,
                syncedToOdooAt: DateTimeOffset.Parse("2026-03-11T11:00:00Z")),
            MakeTransaction(
                TestLegalEntityId,
                TestSiteCode,
                "SYNCED-TOO-OLD-001",
                TransactionStatus.SYNCED_TO_ODOO,
                syncedToOdooAt: DateTimeOffset.Parse("2026-03-11T09:59:59Z")),
            MakeTransaction(
                TestLegalEntityId,
                TestSiteCode,
                "PENDING-EXCLUDED-001",
                TransactionStatus.PENDING),
            MakeTransaction(
                TestLegalEntityId,
                OtherSiteCode,
                "OTHER-SITE-EXCLUDED-001",
                TransactionStatus.SYNCED_TO_ODOO,
                syncedToOdooAt: DateTimeOffset.Parse("2026-03-11T11:30:00Z")),
            MakeTransaction(
                OtherLegalEntityId,
                TestSiteCode,
                "OTHER-TENANT-EXCLUDED-001",
                TransactionStatus.SYNCED_TO_ODOO,
                syncedToOdooAt: DateTimeOffset.Parse("2026-03-11T11:45:00Z")));

        await db.SaveChangesAsync();
    }

    private static Transaction MakeTransaction(
        Guid legalEntityId,
        string siteCode,
        string fccTransactionId,
        TransactionStatus status,
        DateTimeOffset? syncedToOdooAt = null)
    {
        var createdAt = DateTimeOffset.Parse("2026-03-11T09:00:00Z");
        return new Transaction
        {
            Id                     = Guid.NewGuid(),
            CreatedAt              = createdAt,
            LegalEntityId          = legalEntityId,
            FccTransactionId       = fccTransactionId,
            SiteCode               = siteCode,
            PumpNumber             = 1,
            NozzleNumber           = 1,
            ProductCode            = "PMS",
            VolumeMicrolitres      = 30_000_000L,
            AmountMinorUnits       = 24_000_00L,
            UnitPriceMinorPerLitre = 800_00L,
            CurrencyCode           = "GHS",
            StartedAt              = createdAt.AddMinutes(-5),
            CompletedAt            = createdAt,
            FccVendor              = FccVendor.DOMS,
            Status                 = status,
            IngestionSource        = IngestionSource.EDGE_UPLOAD,
            OdooOrderId            = syncedToOdooAt.HasValue ? $"POS/{fccTransactionId}" : null,
            SyncedToOdooAt         = syncedToOdooAt,
            CorrelationId          = Guid.NewGuid(),
            UpdatedAt              = syncedToOdooAt ?? createdAt
        };
    }
}

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

namespace FccMiddleware.IntegrationTests.Ingestion;

/// <summary>
/// Integration tests for POST /api/v1/transactions/upload.
/// Verifies per-record dedup, JWT enforcement, site-claim validation, and batch outcomes.
/// </summary>
[Collection("Integration")]
public sealed class UploadTransactionBatchTests : IAsyncLifetime
{
    // Symmetric key used both to sign test JWTs and configure the WebApplicationFactory.
    private const string TestSigningKey = "TestSigningKey-EdgeAgent-Integration-256bits!!";
    private const string TestIssuer     = "fcc-middleware-cloud";
    private const string TestAudience   = "fcc-middleware-api";

    private static readonly Guid TestLegalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000011");
    private static readonly Guid TestSiteId        = Guid.Parse("99000000-0000-0000-0000-000000000012");
    private static readonly Guid TestFccConfigId   = Guid.Parse("99000000-0000-0000-0000-000000000013");
    private const string TestSiteCode   = "UPLOAD-SITE-001";
    private const string TestDeviceId   = "device-uuid-integration-test";

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
                        ["ConnectionStrings:FccMiddleware"]  = _postgres.GetConnectionString(),
                        ["ConnectionStrings:Redis"]          = _redis.GetConnectionString(),
                        ["DeviceJwt:SigningKey"]             = TestSigningKey,
                        ["DeviceJwt:Issuer"]                 = TestIssuer,
                        ["DeviceJwt:Audience"]               = TestAudience
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
    public async Task Upload_ValidBatch_Returns200WithAllAccepted()
    {
        var token = CreateDeviceJwt(TestDeviceId, TestSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = new
        {
            transactions = new[]
            {
                MakeRecord("UPLOAD-TXN-001", TestSiteCode),
                MakeRecord("UPLOAD-TXN-002", TestSiteCode)
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/transactions/upload", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("acceptedCount").GetInt32().Should().Be(2);
        body.GetProperty("duplicateCount").GetInt32().Should().Be(0);
        body.GetProperty("rejectedCount").GetInt32().Should().Be(0);

        var results = body.GetProperty("results").EnumerateArray().ToList();
        results.Should().HaveCount(2);
        results.All(r => r.GetProperty("outcome").GetString() == "ACCEPTED").Should().BeTrue();
        results.All(r => r.TryGetProperty("transactionId", out _)).Should().BeTrue();

        // Verify stored in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var count = await db.Transactions.IgnoreQueryFilters()
            .CountAsync(t => t.SiteCode == TestSiteCode
                          && (t.FccTransactionId == "UPLOAD-TXN-001" || t.FccTransactionId == "UPLOAD-TXN-002"));
        count.Should().Be(2);
    }

    [Fact]
    public async Task Upload_MixedBatch_NewAndDuplicate_ReturnsCorrectPerRecordStatus()
    {
        var token = CreateDeviceJwt(TestDeviceId, TestSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // First: upload one transaction to create the duplicate baseline
        var firstBatch = new { transactions = new[] { MakeRecord("UPLOAD-DUP-001", TestSiteCode) } };
        var firstResp = await _client.PostAsJsonAsync("/api/v1/transactions/upload", firstBatch);
        firstResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second: upload batch with the same + a new one
        var request = new
        {
            transactions = new[]
            {
                MakeRecord("UPLOAD-DUP-001", TestSiteCode),   // duplicate
                MakeRecord("UPLOAD-NEW-999", TestSiteCode),   // new
                MakeRecord("UPLOAD-DUP-001", TestSiteCode),   // within-batch duplicate of first item
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/transactions/upload", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("acceptedCount").GetInt32().Should().Be(1);
        body.GetProperty("duplicateCount").GetInt32().Should().Be(2);

        var results = body.GetProperty("results").EnumerateArray().ToList();
        results[0].GetProperty("outcome").GetString().Should().Be("DUPLICATE");
        results[1].GetProperty("outcome").GetString().Should().Be("ACCEPTED");
        results[2].GetProperty("outcome").GetString().Should().Be("DUPLICATE");
    }

    [Fact]
    public async Task Upload_WithoutToken_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var request = new { transactions = new[] { MakeRecord("UPLOAD-NOAUTH-001", TestSiteCode) } };

        var response = await _client.PostAsJsonAsync("/api/v1/transactions/upload", request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Upload_ExpiredToken_Returns401()
    {
        var expiredToken = CreateDeviceJwt(
            TestDeviceId, TestSiteCode, TestLegalEntityId,
            notBefore: DateTimeOffset.UtcNow.AddHours(-2),
            expires: DateTimeOffset.UtcNow.AddHours(-1));

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expiredToken);

        var request = new { transactions = new[] { MakeRecord("UPLOAD-EXPIRED-001", TestSiteCode) } };

        var response = await _client.PostAsJsonAsync("/api/v1/transactions/upload", request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Upload_InvalidSignatureToken_Returns401()
    {
        var invalidToken = CreateDeviceJwt(
            TestDeviceId,
            TestSiteCode,
            TestLegalEntityId,
            signingKeyOverride: "WrongSigningKey-EdgeAgent-Integration-256bits!!");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invalidToken);

        var request = new { transactions = new[] { MakeRecord("UPLOAD-BADSIG-001", TestSiteCode) } };

        var response = await _client.PostAsJsonAsync("/api/v1/transactions/upload", request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Upload_SiteMismatch_RecordsRejectedWithSiteMismatchCode()
    {
        // JWT says UPLOAD-SITE-001 but records claim a different site
        var token = CreateDeviceJwt(TestDeviceId, TestSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = new
        {
            transactions = new[]
            {
                MakeRecord("UPLOAD-MISMATCH-001", "WRONG-SITE-999"),  // site mismatch → REJECTED
                MakeRecord("UPLOAD-MISMATCH-002", TestSiteCode)        // correct site  → ACCEPTED
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/transactions/upload", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("rejectedCount").GetInt32().Should().Be(1);
        body.GetProperty("acceptedCount").GetInt32().Should().Be(1);

        var results = body.GetProperty("results").EnumerateArray().ToList();
        results[0].GetProperty("outcome").GetString().Should().Be("REJECTED");
        results[0].GetProperty("errorCode").GetString().Should().Be("SITE_MISMATCH");
        results[1].GetProperty("outcome").GetString().Should().Be("ACCEPTED");
    }

    [Fact]
    public async Task Upload_EmptyBatch_Returns400()
    {
        var token = CreateDeviceJwt(TestDeviceId, TestSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = new { transactions = Array.Empty<object>() };

        var response = await _client.PostAsJsonAsync("/api/v1/transactions/upload", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object MakeRecord(string fccTransactionId, string siteCode) => new
    {
        fccTransactionId,
        siteCode,
        fccVendor              = "DOMS",
        pumpNumber             = 1,
        nozzleNumber           = 1,
        productCode            = "PMS",
        volumeMicrolitres      = 30_000_000L,
        amountMinorUnits       = 24_000_00L,
        unitPriceMinorPerLitre = 800_00L,
        currencyCode           = "GHS",
        startedAt              = "2026-03-11T08:00:00Z",
        completedAt            = "2026-03-11T08:05:00Z"
    };

    private static string CreateDeviceJwt(
        string deviceId,
        string siteCode,
        Guid legalEntityId,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires   = null,
        string? signingKeyOverride = null)
    {
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKeyOverride ?? TestSigningKey));
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
            issuer:            TestIssuer,
            audience:          TestAudience,
            claims:            claims,
            notBefore:         (notBefore ?? now).UtcDateTime,
            expires:           (expires ?? now.AddHours(24)).UtcDateTime,
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
            Name                  = "Upload Test Ghana Ltd",
            CurrencyCode          = "GHS",
            TaxAuthorityCode      = "GRA",
            FiscalizationRequired = false,
            DefaultTimezone       = "Africa/Accra",
            IsActive              = true,
            SyncedAt              = DateTimeOffset.UtcNow,
            CreatedAt             = DateTimeOffset.UtcNow,
            UpdatedAt             = DateTimeOffset.UtcNow
        });

        db.Sites.Add(new Site
        {
            Id               = TestSiteId,
            LegalEntityId    = TestLegalEntityId,
            SiteCode         = TestSiteCode,
            SiteName         = "Upload Integration Test Station",
            OperatingModel   = SiteOperatingModel.COCO,
            CompanyTaxPayerId = "TAX-UPLOAD-001",
            IsActive         = true,
            SyncedAt         = DateTimeOffset.UtcNow,
            CreatedAt        = DateTimeOffset.UtcNow,
            UpdatedAt        = DateTimeOffset.UtcNow
        });

        db.FccConfigs.Add(new FccConfig
        {
            Id                  = TestFccConfigId,
            SiteId              = TestSiteId,
            LegalEntityId       = TestLegalEntityId,
            FccVendor           = FccVendor.DOMS,
            ConnectionProtocol  = ConnectionProtocol.REST,
            HostAddress         = "127.0.0.1",
            Port                = 8080,
            CredentialRef       = "test-cred-upload",
            IngestionMethod     = IngestionMethod.PUSH,
            IngestionMode       = IngestionMode.CLOUD_DIRECT,
            IsActive            = true,
            ConfigVersion       = 1,
            CreatedAt           = DateTimeOffset.UtcNow,
            UpdatedAt           = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }
}

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
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FccMiddleware.IntegrationTests.AgentConfig;

/// <summary>
/// Integration tests for GET /api/v1/agent/config.
/// Verifies config delivery, ETag/304, JWT scoping, and 404 on missing config.
/// </summary>
[Collection("Integration")]
public sealed class GetAgentConfigTests : IAsyncLifetime
{
    private const string TestSigningKey = "TestSigningKey-EdgeAgent-Integration-256bits!!";
    private const string TestIssuer     = "fcc-middleware-cloud";
    private const string TestAudience   = "fcc-middleware-api";

    private static readonly Guid TestLegalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000051");
    private static readonly Guid TestSiteId        = Guid.Parse("99000000-0000-0000-0000-000000000052");
    private static readonly Guid TestFccConfigId   = Guid.Parse("99000000-0000-0000-0000-000000000053");
    private static readonly Guid TestDeviceId      = Guid.Parse("99000000-0000-0000-0000-000000000054");
    private static readonly Guid TestPumpId        = Guid.Parse("99000000-0000-0000-0000-000000000055");
    private static readonly Guid TestNozzleId      = Guid.Parse("99000000-0000-0000-0000-000000000056");
    private static readonly Guid TestProductId     = Guid.Parse("99000000-0000-0000-0000-000000000057");
    private const string TestSiteCode  = "CFG-SITE-001";

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
                        ["DeviceJwt:SigningKey"]             = TestSigningKey,
                        ["DeviceJwt:Issuer"]                 = TestIssuer,
                        ["DeviceJwt:Audience"]               = TestAudience,
                        ["EdgeAgentDefaults:Sync:CloudBaseUrl"] = "https://test.fccmiddleware.com"
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
    public async Task GetConfig_ValidDevice_Returns200WithFullSiteConfig()
    {
        var token = CreateDeviceJwt(TestDeviceId.ToString(), TestSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.GetAsync("/api/v1/agent/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Verify top-level required fields
        body.GetProperty("schemaVersion").GetString().Should().Be("1.0");
        body.GetProperty("configVersion").GetInt32().Should().Be(5);
        body.GetProperty("configId").GetString().Should().Be(TestFccConfigId.ToString());
        body.GetProperty("issuedAtUtc").GetString().Should().NotBeNullOrEmpty();

        // Verify identity section
        var identity = body.GetProperty("identity");
        identity.GetProperty("legalEntityId").GetString().Should().Be(TestLegalEntityId.ToString());
        identity.GetProperty("siteCode").GetString().Should().Be(TestSiteCode);
        identity.GetProperty("siteName").GetString().Should().Be("Config Test Station");
        identity.GetProperty("deviceId").GetString().Should().Be(TestDeviceId.ToString());
        identity.GetProperty("currencyCode").GetString().Should().Be("ZWL");

        // Verify FCC section
        var fcc = body.GetProperty("fcc");
        fcc.GetProperty("enabled").GetBoolean().Should().BeTrue();
        fcc.GetProperty("vendor").GetString().Should().Be("DOMS");
        fcc.GetProperty("hostAddress").GetString().Should().Be("192.168.1.100");
        fcc.GetProperty("port").GetInt32().Should().Be(9090);
        fcc.GetProperty("catchUpPullIntervalSeconds").GetInt32().Should().Be(30);

        // Verify mappings section has nozzle data
        var mappings = body.GetProperty("mappings");
        var nozzles = mappings.GetProperty("nozzles").EnumerateArray().ToList();
        nozzles.Should().HaveCount(1);
        nozzles[0].GetProperty("odooPumpNumber").GetInt32().Should().Be(1);
        nozzles[0].GetProperty("fccPumpNumber").GetInt32().Should().Be(1);
        nozzles[0].GetProperty("odooNozzleNumber").GetInt32().Should().Be(1);
        nozzles[0].GetProperty("fccNozzleNumber").GetInt32().Should().Be(1);
        nozzles[0].GetProperty("productCode").GetString().Should().Be("PMS");

        var products = mappings.GetProperty("products").EnumerateArray().ToList();
        products.Should().HaveCount(1);
        products[0].GetProperty("canonicalProductCode").GetString().Should().Be("PMS");

        // Verify sync section uses config override
        var sync = body.GetProperty("sync");
        sync.GetProperty("cloudBaseUrl").GetString().Should().Be("https://test.fccmiddleware.com");

        // Verify ETag header is set
        response.Headers.ETag.Should().NotBeNull();
        response.Headers.ETag!.Tag.Should().Be("\"5\"");
    }

    [Fact]
    public async Task GetConfig_WithMatchingETag_Returns304NotModified()
    {
        var token = CreateDeviceJwt(TestDeviceId.ToString(), TestSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // First request to get current version
        var firstResponse = await _client.GetAsync("/api/v1/agent/config");
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second request with If-None-Match matching config version
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/config");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"5\""));

        var secondResponse = await _client.SendAsync(request);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task GetConfig_WithStaleETag_Returns200WithNewConfig()
    {
        var token = CreateDeviceJwt(TestDeviceId.ToString(), TestSiteCode, TestLegalEntityId);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/config");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"3\"")); // older version

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("configVersion").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task GetConfig_WithoutToken_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await _client.GetAsync("/api/v1/agent/config");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetConfig_UnknownDevice_Returns404()
    {
        var unknownDeviceId = Guid.NewGuid();
        var token = CreateDeviceJwt(unknownDeviceId.ToString(), TestSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.GetAsync("/api/v1/agent/config");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("DEVICE_NOT_FOUND");
    }

    [Fact]
    public async Task GetConfig_DeviceRegisteredAtDifferentSite_Returns401()
    {
        // JWT claims site "WRONG-SITE" but device is registered at CFG-SITE-001
        var token = CreateDeviceJwt(TestDeviceId.ToString(), "WRONG-SITE", TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.GetAsync("/api/v1/agent/config");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("SITE_MISMATCH");
    }

    [Fact]
    public async Task GetConfig_DeviceRegisteredUnderDifferentLegalEntity_Returns401()
    {
        var wrongLegalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000099");
        var token = CreateDeviceJwt(TestDeviceId.ToString(), TestSiteCode, wrongLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.GetAsync("/api/v1/agent/config");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("SITE_MISMATCH");
    }

    [Fact]
    public async Task GetConfig_SiteWithNoFccConfig_Returns404()
    {
        // Create a site with no FccConfig
        var noConfigSiteId = Guid.Parse("99000000-0000-0000-0000-000000000060");
        var noConfigDeviceId = Guid.Parse("99000000-0000-0000-0000-000000000061");
        const string noConfigSiteCode = "NO-CFG-SITE-001";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

            if (!await db.Sites.IgnoreQueryFilters().AnyAsync(s => s.Id == noConfigSiteId))
            {
                db.Sites.Add(new Site
                {
                    Id                = noConfigSiteId,
                    LegalEntityId     = TestLegalEntityId,
                    SiteCode          = noConfigSiteCode,
                    SiteName          = "Site Without Config",
                    OperatingModel    = SiteOperatingModel.COCO,
                    CompanyTaxPayerId = "TAX-NOCFG-001",
                    IsActive          = true,
                    SyncedAt          = DateTimeOffset.UtcNow,
                    CreatedAt         = DateTimeOffset.UtcNow,
                    UpdatedAt         = DateTimeOffset.UtcNow
                });

                db.AgentRegistrations.Add(new AgentRegistration
                {
                    Id                 = noConfigDeviceId,
                    SiteId             = noConfigSiteId,
                    LegalEntityId      = TestLegalEntityId,
                    SiteCode           = noConfigSiteCode,
                    DeviceSerialNumber = "SN-NOCFG-001",
                    DeviceModel        = "Urovo DT50",
                    OsVersion          = "Android 12",
                    AgentVersion       = "1.0.0",
                    IsActive           = true,
                    TokenHash          = "nocfg-hash",
                    TokenExpiresAt     = DateTimeOffset.UtcNow.AddDays(30),
                    RegisteredAt       = DateTimeOffset.UtcNow,
                    CreatedAt          = DateTimeOffset.UtcNow,
                    UpdatedAt          = DateTimeOffset.UtcNow
                });

                await db.SaveChangesAsync();
            }
        }

        var token = CreateDeviceJwt(noConfigDeviceId.ToString(), noConfigSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.GetAsync("/api/v1/agent/config");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("CONFIG_NOT_FOUND");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CreateDeviceJwt(
        string deviceId,
        string siteCode,
        Guid legalEntityId,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires   = null)
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
            issuer:             TestIssuer,
            audience:           TestAudience,
            claims:             claims,
            notBefore:          (notBefore ?? now).UtcDateTime,
            expires:            (expires ?? now.AddHours(24)).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task SeedTestDataAsync(FccMiddlewareDbContext db)
    {
        if (await db.LegalEntities.IgnoreQueryFilters().AnyAsync(e => e.Id == TestLegalEntityId)) return;

        db.LegalEntities.Add(new LegalEntity
        {
            Id                    = TestLegalEntityId,
            CountryCode           = "ZW",
            Name                  = "Config Test Zimbabwe Ltd",
            CurrencyCode          = "ZWL",
            TaxAuthorityCode      = "ZIMRA",
            FiscalizationRequired = true,
            DefaultTimezone       = "Africa/Harare",
            IsActive              = true,
            SyncedAt              = DateTimeOffset.UtcNow,
            CreatedAt             = DateTimeOffset.UtcNow,
            UpdatedAt             = DateTimeOffset.UtcNow
        });

        db.Sites.Add(new Site
        {
            Id                = TestSiteId,
            LegalEntityId     = TestLegalEntityId,
            SiteCode          = TestSiteCode,
            SiteName          = "Config Test Station",
            OperatingModel    = SiteOperatingModel.COCO,
            CompanyTaxPayerId = "TAX-CFG-001",
            OdooSiteId        = "ODOO-CFG-001",
            IsActive          = true,
            SyncedAt          = DateTimeOffset.UtcNow,
            CreatedAt         = DateTimeOffset.UtcNow,
            UpdatedAt         = DateTimeOffset.UtcNow
        });

        db.Products.Add(new Product
        {
            Id            = TestProductId,
            LegalEntityId = TestLegalEntityId,
            ProductCode   = "PMS",
            ProductName   = "Premium Motor Spirit",
            UnitOfMeasure = "LITRE",
            IsActive      = true,
            SyncedAt      = DateTimeOffset.UtcNow,
            CreatedAt     = DateTimeOffset.UtcNow,
            UpdatedAt     = DateTimeOffset.UtcNow
        });

        db.Pumps.Add(new Pump
        {
            Id            = TestPumpId,
            SiteId        = TestSiteId,
            LegalEntityId = TestLegalEntityId,
            PumpNumber    = 1,
            FccPumpNumber = 1,
            IsActive      = true,
            SyncedAt      = DateTimeOffset.UtcNow,
            CreatedAt     = DateTimeOffset.UtcNow,
            UpdatedAt     = DateTimeOffset.UtcNow
        });

        db.Nozzles.Add(new Nozzle
        {
            Id               = TestNozzleId,
            PumpId           = TestPumpId,
            SiteId           = TestSiteId,
            LegalEntityId    = TestLegalEntityId,
            OdooNozzleNumber = 1,
            FccNozzleNumber  = 1,
            ProductId        = TestProductId,
            IsActive         = true,
            SyncedAt         = DateTimeOffset.UtcNow,
            CreatedAt        = DateTimeOffset.UtcNow,
            UpdatedAt        = DateTimeOffset.UtcNow
        });

        db.FccConfigs.Add(new FccConfig
        {
            Id                 = TestFccConfigId,
            SiteId             = TestSiteId,
            LegalEntityId      = TestLegalEntityId,
            FccVendor          = FccVendor.DOMS,
            ConnectionProtocol = ConnectionProtocol.REST,
            HostAddress        = "192.168.1.100",
            Port               = 9090,
            CredentialRef      = "test-cred-config",
            IngestionMethod    = IngestionMethod.PUSH,
            IngestionMode      = IngestionMode.CLOUD_DIRECT,
            IsActive           = true,
            ConfigVersion      = 5,
            CreatedAt          = DateTimeOffset.UtcNow,
            UpdatedAt          = DateTimeOffset.UtcNow
        });

        db.AgentRegistrations.Add(new AgentRegistration
        {
            Id                 = TestDeviceId,
            SiteId             = TestSiteId,
            LegalEntityId      = TestLegalEntityId,
            SiteCode           = TestSiteCode,
            DeviceSerialNumber = "SN-CFG-001",
            DeviceModel        = "Urovo DT50",
            OsVersion          = "Android 12",
            AgentVersion       = "1.0.0",
            IsActive           = true,
            TokenHash          = "test-config-hash",
            TokenExpiresAt     = DateTimeOffset.UtcNow.AddDays(30),
            RegisteredAt       = DateTimeOffset.UtcNow,
            CreatedAt          = DateTimeOffset.UtcNow,
            UpdatedAt          = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }
}

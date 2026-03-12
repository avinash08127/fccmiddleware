using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FccMiddleware.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FccMiddleware.IntegrationTests.AgentConfig;

[Collection("Integration")]
public sealed class CheckAgentVersionTests : IAsyncLifetime
{
    private const string TestSigningKey = "TestSigningKey-VersionCheck-256bits!!!!!!";
    private const string TestIssuer = "fcc-middleware-cloud";
    private const string TestAudience = "fcc-middleware-api";
    private static readonly Guid TestLegalEntityId = Guid.Parse("88000000-0000-0000-0000-000000000001");
    private static readonly Guid TestSiteId = Guid.Parse("88000000-0000-0000-0000-000000000002");
    private static readonly Guid TestDeviceId = Guid.Parse("88000000-0000-0000-0000-000000000003");
    private const string TestSiteCode = "VER-SITE-001";

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
                        ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
                        ["DeviceJwt:SigningKey"] = TestSigningKey,
                        ["DeviceJwt:Issuer"] = TestIssuer,
                        ["DeviceJwt:Audience"] = TestAudience,
                        ["EdgeAgentDefaults:Rollout:MinAgentVersion"] = "1.2.0",
                        ["EdgeAgentDefaults:Rollout:LatestAgentVersion"] = "1.4.0",
                        ["EdgeAgentDefaults:Rollout:UpdateUrl"] = "https://downloads.example.com/edge-agent.apk",
                        ["EdgeAgentDefaults:Rollout:LatestReleaseNotes"] = "Bug fixes and protocol updates."
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
    public async Task CheckVersion_CompatibleVersion_ReturnsCompatibleTrue()
    {
        Authorize();

        var response = await _client.GetAsync("/api/v1/agent/version-check?appVersion=1.3.0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("compatible").GetBoolean().Should().BeTrue();
        body.GetProperty("minimumVersion").GetString().Should().Be("1.2.0");
        body.GetProperty("latestVersion").GetString().Should().Be("1.4.0");
        body.GetProperty("updateRequired").GetBoolean().Should().BeFalse();
        body.GetProperty("updateAvailable").GetBoolean().Should().BeTrue();
        body.GetProperty("updateUrl").GetString().Should().Be("https://downloads.example.com/edge-agent.apk");
    }

    [Fact]
    public async Task CheckVersion_OldVersion_ReturnsUpdateRequiredTrue()
    {
        Authorize();

        var response = await _client.GetAsync("/api/v1/agent/version-check?appVersion=1.1.9");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("compatible").GetBoolean().Should().BeFalse();
        body.GetProperty("updateRequired").GetBoolean().Should().BeTrue();
        body.GetProperty("minimumVersion").GetString().Should().Be("1.2.0");
        body.GetProperty("latestVersion").GetString().Should().Be("1.4.0");
    }

    [Fact]
    public async Task CheckVersion_AgentVersionAlias_IsAccepted()
    {
        Authorize();

        var response = await _client.GetAsync("/api/v1/agent/version-check?agentVersion=1.4.0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("compatible").GetBoolean().Should().BeTrue();
        body.GetProperty("agentVersion").GetString().Should().Be("1.4.0");
    }

    [Fact]
    public async Task CheckVersion_MissingVersion_Returns400()
    {
        Authorize();

        var response = await _client.GetAsync("/api/v1/agent/version-check");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("INVALID_AGENT_VERSION");
    }

    [Fact]
    public async Task CheckVersion_WithoutToken_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await _client.GetAsync("/api/v1/agent/version-check?appVersion=1.3.0");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private void Authorize()
    {
        var token = CreateDeviceJwt(TestDeviceId.ToString(), TestSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private string CreateDeviceJwt(string deviceId, string siteCode, Guid legalEntityId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, deviceId),
            new(ClaimTypes.NameIdentifier, deviceId),
            new("site", siteCode),
            new("lei", legalEntityId.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task SeedTestDataAsync(FccMiddlewareDbContext db)
    {
        if (!await db.LegalEntities.IgnoreQueryFilters().AnyAsync(entity => entity.Id == TestLegalEntityId))
        {
            db.LegalEntities.Add(new Domain.Entities.LegalEntity
            {
                Id = TestLegalEntityId,
                BusinessCode = "ZW-001",
                Name = "Version Check Legal Entity",
                CountryCode = "ZW",
                CountryName = "Zimbabwe",
                CurrencyCode = "ZWL",
                TaxAuthorityCode = "ZIMRA",
                FiscalizationRequired = false,
                DefaultTimezone = "Africa/Harare",
                OdooCompanyId = "ODOO-ZW-001",
                SyncedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        if (!await db.Sites.IgnoreQueryFilters().AnyAsync(site => site.Id == TestSiteId))
        {
            db.Sites.Add(new Domain.Entities.Site
            {
                Id = TestSiteId,
                LegalEntityId = TestLegalEntityId,
                SiteCode = TestSiteCode,
                SiteName = "Version Check Site",
                OperatingModel = Domain.Enums.SiteOperatingModel.COCO,
                SiteUsesPreAuth = false,
                CompanyTaxPayerId = "TAX-VER-001",
                IsActive = true,
                SyncedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        if (!await db.AgentRegistrations.IgnoreQueryFilters().AnyAsync(agent => agent.Id == TestDeviceId))
        {
            db.AgentRegistrations.Add(new Domain.Entities.AgentRegistration
            {
                Id = TestDeviceId,
                SiteId = TestSiteId,
                SiteCode = TestSiteCode,
                LegalEntityId = TestLegalEntityId,
                DeviceSerialNumber = "SN-VER-001",
                DeviceModel = "Urovo i9100",
                OsVersion = "12",
                AgentVersion = "1.2.0",
                IsActive = true,
                TokenHash = "hash",
                TokenExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                RegisteredAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
                DeactivatedAt = null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
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
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FccMiddleware.IntegrationTests.Registration;

/// <summary>
/// Integration tests for the full device registration flow:
/// bootstrap token generation → device registration → token refresh → decommission.
/// </summary>
[Collection("Integration")]
public sealed class DeviceRegistrationTests : IAsyncLifetime
{
    private const string TestSigningKey = "TestSigningKey-Registration-Integration-256bits!!";
    private const string TestIssuer     = "fcc-middleware-cloud";
    private const string TestAudience   = "fcc-middleware-api";
    private const string TestPortalSigningKey = "TestSigningKey-Portal-Registration-256bits!!";
    private const string TestPortalIssuer = "https://login.microsoftonline.com/test-tenant-id/v2.0";
    private const string TestPortalAudience = "00000000-0000-0000-0000-000000000321";

    private static readonly Guid TestLegalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000021");
    private static readonly Guid TestSiteId        = Guid.Parse("99000000-0000-0000-0000-000000000022");
    private static readonly Guid TestFccConfigId   = Guid.Parse("99000000-0000-0000-0000-000000000023");
    private const string TestSiteCode = "REG-SITE-001";

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
                        ["PortalJwt:SigningKey"]             = TestPortalSigningKey,
                        ["PortalJwt:Authority"]              = TestPortalIssuer,
                        ["PortalJwt:Audience"]               = TestPortalAudience,
                        ["PortalJwt:ClientId"]               = TestPortalAudience
                    });
                });
            });

        _ = _factory.Server;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        await db.Database.EnsureCreatedAsync();
        await SeedTestDataAsync(db);

        _client = _factory.CreateClient();
        SetPortalAuth("SystemAdmin", "portal-admin", TestLegalEntityId);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.StopAsync(), _redis.StopAsync());
    }

    // ── Bootstrap Token Tests ──────────────────────────────────────────────

    [Fact]
    public async Task GenerateBootstrapToken_ValidRequest_Returns201WithToken()
    {
        var request = new { siteCode = TestSiteCode, legalEntityId = TestLegalEntityId };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/bootstrap-tokens", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("tokenId").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("rawToken").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("expiresAt").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("siteCode").GetString().Should().Be(TestSiteCode);
    }

    [Fact]
    public async Task GenerateBootstrapToken_WithoutPortalAuth_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await _client.PostAsJsonAsync("/api/v1/admin/bootstrap-tokens",
            new { siteCode = TestSiteCode, legalEntityId = TestLegalEntityId });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        SetPortalAuth("SystemAdmin", "portal-admin", TestLegalEntityId);
    }

    [Fact]
    public async Task GenerateBootstrapToken_PortalUserWithoutAdminRole_Returns403()
    {
        SetPortalAuth("SiteSupervisor", "portal-user", TestLegalEntityId);

        var response = await _client.PostAsJsonAsync("/api/v1/admin/bootstrap-tokens",
            new { siteCode = TestSiteCode, legalEntityId = TestLegalEntityId });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        SetPortalAuth("SystemAdmin", "portal-admin", TestLegalEntityId);
    }

    [Fact]
    public async Task GenerateBootstrapToken_InvalidSite_Returns404()
    {
        var request = new { siteCode = "NONEXISTENT-SITE", legalEntityId = TestLegalEntityId };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/bootstrap-tokens", request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Registration Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task Register_FullFlow_Returns201WithDeviceCredentials()
    {
        // 1. Generate bootstrap token
        var bootstrapToken = await GenerateBootstrapTokenAsync();

        // 2. Register device
        var registerRequest = new
        {
            siteCode = TestSiteCode,
            deviceSerialNumber = "SN-001",
            deviceModel = "Urovo i9100",
            osVersion = "12.0",
            agentVersion = "1.0.0"
        };

        var registerMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/register")
        {
            Content = JsonContent.Create(registerRequest)
        };
        registerMsg.Headers.Add("X-Provisioning-Token", bootstrapToken);

        var response = await _client.SendAsync(registerMsg);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("deviceId").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("deviceToken").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("refreshToken").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("siteCode").GetString().Should().Be(TestSiteCode);
        body.GetProperty("legalEntityId").GetString().Should().Be(TestLegalEntityId.ToString());
        body.GetProperty("siteConfig").GetProperty("configId").GetString().Should().Be(TestFccConfigId.ToString());
        body.GetProperty("siteConfig").GetProperty("configVersion").GetInt32().Should().Be(1);
        body.GetProperty("siteConfig").GetProperty("identity").GetProperty("siteCode").GetString().Should().Be(TestSiteCode);

        // Verify JWT claims
        var jwt = body.GetProperty("deviceToken").GetString()!;
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(jwt);
        token.Claims.First(c => c.Type == "site").Value.Should().Be(TestSiteCode);
        token.Claims.First(c => c.Type == "lei").Value.Should().Be(TestLegalEntityId.ToString());

        // Verify DB record
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var deviceId = Guid.Parse(body.GetProperty("deviceId").GetString()!);
        var reg = await db.AgentRegistrations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == deviceId);
        reg.Should().NotBeNull();
        reg!.IsActive.Should().BeTrue();
        reg.SiteCode.Should().Be(TestSiteCode);
    }

    [Fact]
    public async Task Register_WithoutProvisioningToken_Returns401()
    {
        var registerRequest = new
        {
            siteCode = TestSiteCode,
            deviceSerialNumber = "SN-002",
            deviceModel = "Test",
            osVersion = "12.0",
            agentVersion = "1.0.0"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/agent/register", registerRequest);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("BOOTSTRAP_TOKEN_MISSING");
    }

    [Fact]
    public async Task Register_UsedBootstrapToken_Returns409()
    {
        var bootstrapToken = await GenerateBootstrapTokenAsync();

        // Use it once
        var registerRequest = new
        {
            siteCode = TestSiteCode,
            deviceSerialNumber = "SN-USED-001",
            deviceModel = "Test",
            osVersion = "12.0",
            agentVersion = "1.0.0"
        };

        var msg1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/register")
        {
            Content = JsonContent.Create(registerRequest)
        };
        msg1.Headers.Add("X-Provisioning-Token", bootstrapToken);
        var firstResponse = await _client.SendAsync(msg1);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Try to use it again (need a new token since first registered an agent,
        // and we need replacePreviousAgent for the site conflict, but the token itself
        // is the issue here)
        var msg2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/register")
        {
            Content = JsonContent.Create(new
            {
                siteCode = TestSiteCode,
                deviceSerialNumber = "SN-USED-002",
                deviceModel = "Test",
                osVersion = "12.0",
                agentVersion = "1.0.0",
                replacePreviousAgent = true
            })
        };
        msg2.Headers.Add("X-Provisioning-Token", bootstrapToken);
        var secondResponse = await _client.SendAsync(msg2);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("BOOTSTRAP_TOKEN_ALREADY_USED");
    }

    [Fact]
    public async Task Register_ExpiredBootstrapToken_Returns401()
    {
        // Manually insert an expired bootstrap token
        var rawToken = "expired-test-token-value";
        var tokenHash = ComputeSha256Hex(rawToken);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            db.BootstrapTokens.Add(new BootstrapToken
            {
                Id = Guid.NewGuid(),
                LegalEntityId = TestLegalEntityId,
                SiteCode = TestSiteCode,
                TokenHash = tokenHash,
                Status = ProvisioningTokenStatus.ACTIVE,
                CreatedBy = "test",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1), // already expired
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-73)
            });
            await db.SaveChangesAsync();
        }

        var registerRequest = new
        {
            siteCode = TestSiteCode,
            deviceSerialNumber = "SN-EXP-001",
            deviceModel = "Test",
            osVersion = "12.0",
            agentVersion = "1.0.0"
        };

        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/register")
        {
            Content = JsonContent.Create(registerRequest)
        };
        msg.Headers.Add("X-Provisioning-Token", rawToken);
        var response = await _client.SendAsync(msg);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("BOOTSTRAP_TOKEN_EXPIRED");
    }

    [Fact]
    public async Task Register_ActiveAgentExists_WithoutReplace_Returns409()
    {
        // Register first device
        var token1 = await GenerateBootstrapTokenAsync();
        var msg1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/register")
        {
            Content = JsonContent.Create(new
            {
                siteCode = TestSiteCode,
                deviceSerialNumber = "SN-CONFLICT-001",
                deviceModel = "Test",
                osVersion = "12.0",
                agentVersion = "1.0.0"
            })
        };
        msg1.Headers.Add("X-Provisioning-Token", token1);
        var first = await _client.SendAsync(msg1);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // Try second without replace
        var token2 = await GenerateBootstrapTokenAsync();
        var msg2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/register")
        {
            Content = JsonContent.Create(new
            {
                siteCode = TestSiteCode,
                deviceSerialNumber = "SN-CONFLICT-002",
                deviceModel = "Test",
                osVersion = "12.0",
                agentVersion = "1.0.0",
                replacePreviousAgent = false
            })
        };
        msg2.Headers.Add("X-Provisioning-Token", token2);
        var second = await _client.SendAsync(msg2);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("ACTIVE_AGENT_EXISTS");
    }

    [Fact]
    public async Task Register_ActiveAgentExists_WithReplace_Returns201()
    {
        // Register first device
        var token1 = await GenerateBootstrapTokenAsync();
        var msg1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/register")
        {
            Content = JsonContent.Create(new
            {
                siteCode = TestSiteCode,
                deviceSerialNumber = "SN-REPLACE-001",
                deviceModel = "Test",
                osVersion = "12.0",
                agentVersion = "1.0.0"
            })
        };
        msg1.Headers.Add("X-Provisioning-Token", token1);
        var first = await _client.SendAsync(msg1);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstDeviceId = Guid.Parse(firstBody.GetProperty("deviceId").GetString()!);

        // Register second with replace
        var token2 = await GenerateBootstrapTokenAsync();
        var msg2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/register")
        {
            Content = JsonContent.Create(new
            {
                siteCode = TestSiteCode,
                deviceSerialNumber = "SN-REPLACE-002",
                deviceModel = "Test",
                osVersion = "12.0",
                agentVersion = "1.0.0",
                replacePreviousAgent = true
            })
        };
        msg2.Headers.Add("X-Provisioning-Token", token2);
        var second = await _client.SendAsync(msg2);

        second.StatusCode.Should().Be(HttpStatusCode.Created);

        // Verify old device is deactivated
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var oldAgent = await db.AgentRegistrations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == firstDeviceId);
        oldAgent!.IsActive.Should().BeFalse();
        oldAgent.DeactivatedAt.Should().NotBeNull();
    }

    // ── Token Refresh Tests ─────────────────────────────────────────────────

    [Fact]
    public async Task RefreshToken_ValidToken_ReturnsNewTokenPair()
    {
        // Register a device to get a refresh token
        var (_, refreshToken) = await RegisterDeviceAsync("SN-REFRESH-001");

        // Refresh
        var refreshResponse = await _client.PostAsJsonAsync("/api/v1/agent/token/refresh",
            new { refreshToken });

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await refreshResponse.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("deviceToken").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("refreshToken").GetString().Should().NotBeNullOrEmpty();
        // New refresh token should be different (rotation)
        body.GetProperty("refreshToken").GetString().Should().NotBe(refreshToken);
    }

    [Fact]
    public async Task RefreshToken_UsedTokenAfterRotation_Returns401()
    {
        var (_, refreshToken) = await RegisterDeviceAsync("SN-ROTATION-001");

        // First refresh succeeds
        var firstRefresh = await _client.PostAsJsonAsync("/api/v1/agent/token/refresh",
            new { refreshToken });
        firstRefresh.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second refresh with old token fails (it was revoked during rotation)
        var secondRefresh = await _client.PostAsJsonAsync("/api/v1/agent/token/refresh",
            new { refreshToken });
        secondRefresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RefreshToken_InvalidToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/agent/token/refresh",
            new { refreshToken = "invalid-token-value" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Decommission Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task Decommission_ActiveDevice_Returns200AndRevokesTokens()
    {
        var (deviceId, refreshToken) = await RegisterDeviceAsync("SN-DECOM-001");

        // Decommission
        var decommResponse = await _client.PostAsync(
            $"/api/v1/admin/agent/{deviceId}/decommission", null);

        decommResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await decommResponse.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("deviceId").GetString().Should().Be(deviceId.ToString());
        body.GetProperty("deactivatedAt").GetString().Should().NotBeNullOrEmpty();

        // Verify device is deactivated
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var agent = await db.AgentRegistrations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == deviceId);
        agent!.IsActive.Should().BeFalse();

        // Verify refresh token is revoked — refresh should fail with DEVICE_DECOMMISSIONED
        var refreshResponse = await _client.PostAsJsonAsync("/api/v1/agent/token/refresh",
            new { refreshToken });
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Decommission_WithoutPortalAuth_Returns401()
    {
        var (deviceId, _) = await RegisterDeviceAsync("SN-DECOM-NOAUTH-001");
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await _client.PostAsync($"/api/v1/admin/agent/{deviceId}/decommission", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        SetPortalAuth("SystemAdmin", "portal-admin", TestLegalEntityId);
    }

    [Fact]
    public async Task Decommission_AlreadyDecommissioned_Returns409()
    {
        var (deviceId, _) = await RegisterDeviceAsync("SN-DECOM-TWICE-001");

        // First decommission
        var first = await _client.PostAsync($"/api/v1/admin/agent/{deviceId}/decommission", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second decommission
        var second = await _client.PostAsync($"/api/v1/admin/agent/{deviceId}/decommission", null);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("DEVICE_ALREADY_DECOMMISSIONED");
    }

    [Fact]
    public async Task Decommission_NonExistentDevice_Returns404()
    {
        var response = await _client.PostAsync(
            $"/api/v1/admin/agent/{Guid.NewGuid()}/decommission", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── End-to-End: Decommissioned device cannot use JWT ────────────────────

    [Fact]
    public async Task DecommissionedDevice_JwtStillValidButRefreshFails()
    {
        var (deviceId, refreshToken) = await RegisterDeviceAsync("SN-E2E-DECOM-001");

        // Decommission
        var decomm = await _client.PostAsync($"/api/v1/admin/agent/{deviceId}/decommission", null);
        decomm.StatusCode.Should().Be(HttpStatusCode.OK);

        // Try to refresh — should get 401 (token revoked, then device decommissioned check)
        var refreshResp = await _client.PostAsJsonAsync("/api/v1/agent/token/refresh",
            new { refreshToken });
        // Revoked token returns Unauthorized
        refreshResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> GenerateBootstrapTokenAsync()
    {
        SetPortalAuth("SystemAdmin", "portal-admin", TestLegalEntityId);
        var request = new { siteCode = TestSiteCode, legalEntityId = TestLegalEntityId };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/bootstrap-tokens", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("rawToken").GetString()!;
    }

    private async Task<(Guid DeviceId, string RefreshToken)> RegisterDeviceAsync(string serialNumber)
    {
        // Each registration needs: deactivate old agent if present, so use replacePreviousAgent
        var bootstrapToken = await GenerateBootstrapTokenAsync();

        var registerRequest = new
        {
            siteCode = TestSiteCode,
            deviceSerialNumber = serialNumber,
            deviceModel = "Test Device",
            osVersion = "12.0",
            agentVersion = "1.0.0",
            replacePreviousAgent = true
        };

        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/register")
        {
            Content = JsonContent.Create(registerRequest)
        };
        msg.Headers.Add("X-Provisioning-Token", bootstrapToken);

        var response = await _client.SendAsync(msg);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var deviceId = Guid.Parse(body.GetProperty("deviceId").GetString()!);
        var refreshToken = body.GetProperty("refreshToken").GetString()!;

        return (deviceId, refreshToken);
    }

    private static string ComputeSha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void SetPortalAuth(string role, string oid, params Guid[] legalEntityIds)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(TestPortalSigningKey);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, oid),
            new("oid", oid),
            new("roles", role),
            new("legal_entities", string.Join(",", legalEntityIds))
        };

        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(1),
            Issuer = TestPortalIssuer,
            Audience = TestPortalAudience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)
        });

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenHandler.WriteToken(token));
    }

    private static async Task SeedTestDataAsync(FccMiddlewareDbContext db)
    {
        if (await db.LegalEntities.IgnoreQueryFilters().AnyAsync(e => e.Id == TestLegalEntityId)) return;

        db.LegalEntities.Add(new LegalEntity
        {
            Id                    = TestLegalEntityId,
            CountryCode           = "KE",
            Name                  = "Registration Test Kenya Ltd",
            CurrencyCode          = "KES",
            TaxAuthorityCode      = "KRA",
            FiscalizationRequired = false,
            DefaultTimezone       = "Africa/Nairobi",
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
            SiteName          = "Registration Integration Test Station",
            OperatingModel    = SiteOperatingModel.COCO,
            CompanyTaxPayerId = "TAX-REG-001",
            IsActive          = true,
            SyncedAt          = DateTimeOffset.UtcNow,
            CreatedAt         = DateTimeOffset.UtcNow,
            UpdatedAt         = DateTimeOffset.UtcNow
        });

        db.FccConfigs.Add(new FccConfig
        {
            Id                 = TestFccConfigId,
            SiteId             = TestSiteId,
            LegalEntityId      = TestLegalEntityId,
            FccVendor          = FccVendor.DOMS,
            ConnectionProtocol = ConnectionProtocol.REST,
            HostAddress        = "127.0.0.1",
            Port               = 8080,
            CredentialRef      = "reg-test-cred",
            IngestionMethod    = IngestionMethod.PULL,
            IngestionMode      = IngestionMode.CLOUD_DIRECT,
            IsActive           = true,
            ConfigVersion      = 1,
            PullIntervalSeconds = 30,
            HeartbeatIntervalSeconds = 30,
            CreatedAt          = DateTimeOffset.UtcNow,
            UpdatedAt          = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }
}

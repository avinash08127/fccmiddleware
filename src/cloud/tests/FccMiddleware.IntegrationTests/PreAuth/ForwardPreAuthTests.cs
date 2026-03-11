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

namespace FccMiddleware.IntegrationTests.PreAuth;

/// <summary>
/// Integration tests for POST /api/v1/preauth.
/// Verifies pre-auth creation, dedup, status transitions, terminal re-request, and JWT enforcement.
/// </summary>
[Collection("Integration")]
public sealed class ForwardPreAuthTests : IAsyncLifetime
{
    private const string TestSigningKey = "TestSigningKey-EdgeAgent-Integration-256bits!!";
    private const string TestIssuer     = "fcc-middleware-cloud";
    private const string TestAudience   = "fcc-middleware-api";

    private static readonly Guid TestLegalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000021");
    private static readonly Guid TestSiteId        = Guid.Parse("99000000-0000-0000-0000-000000000022");
    private static readonly Guid TestFccConfigId   = Guid.Parse("99000000-0000-0000-0000-000000000023");
    private const string TestSiteCode = "PREAUTH-SITE-001";
    private const string TestDeviceId = "device-uuid-preauth-test";

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
    public async Task ForwardPreAuth_NewRecord_Returns201()
    {
        SetAuthHeader();

        var request = MakeRequest("ODOO-ORDER-001", "PENDING");
        var response = await _client.PostAsJsonAsync("/api/v1/preauth", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("status").GetString().Should().Be("PENDING");
        body.GetProperty("siteCode").GetString().Should().Be(TestSiteCode);
        body.GetProperty("odooOrderId").GetString().Should().Be("ODOO-ORDER-001");

        // Verify stored in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var record = await db.PreAuthRecords.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.OdooOrderId == "ODOO-ORDER-001" && p.SiteCode == TestSiteCode);
        record.Should().NotBeNull();
        record!.Status.Should().Be(PreAuthStatus.PENDING);
        record.RequestedAmountMinorUnits.Should().Be(500_00L);
    }

    [Fact]
    public async Task ForwardPreAuth_NewRecordWithAuthorizedStatus_Returns201()
    {
        SetAuthHeader();

        var request = MakeRequest("ODOO-ORDER-AUTHZ-001", "AUTHORIZED",
            fccCorrelationId: "CORR-001", fccAuthorizationCode: "AUTH-CODE-001");
        var response = await _client.PostAsJsonAsync("/api/v1/preauth", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("AUTHORIZED");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var record = await db.PreAuthRecords.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.OdooOrderId == "ODOO-ORDER-AUTHZ-001");
        record.Should().NotBeNull();
        record!.Status.Should().Be(PreAuthStatus.AUTHORIZED);
        record.FccCorrelationId.Should().Be("CORR-001");
        record.FccAuthorizationCode.Should().Be("AUTH-CODE-001");
        record.AuthorizedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ForwardPreAuth_DuplicateIdempotent_Returns200WithExisting()
    {
        SetAuthHeader();

        // Create initial record
        var request = MakeRequest("ODOO-ORDER-DUP-001", "PENDING");
        var first = await _client.PostAsJsonAsync("/api/v1/preauth", request);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // Re-post same key+status → idempotent 200
        var second = await _client.PostAsJsonAsync("/api/v1/preauth", request);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("PENDING");
    }

    [Fact]
    public async Task ForwardPreAuth_StatusTransition_Returns200()
    {
        SetAuthHeader();

        // Create PENDING record
        var create = MakeRequest("ODOO-ORDER-TRANS-001", "PENDING");
        var first = await _client.PostAsJsonAsync("/api/v1/preauth", create);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // Transition to AUTHORIZED
        var update = MakeRequest("ODOO-ORDER-TRANS-001", "AUTHORIZED",
            fccCorrelationId: "CORR-TRANS", fccAuthorizationCode: "AUTH-TRANS");
        var second = await _client.PostAsJsonAsync("/api/v1/preauth", update);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("AUTHORIZED");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var record = await db.PreAuthRecords.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.OdooOrderId == "ODOO-ORDER-TRANS-001");
        record!.Status.Should().Be(PreAuthStatus.AUTHORIZED);
        record.AuthorizedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ForwardPreAuth_InvalidTransition_Returns409()
    {
        SetAuthHeader();

        // Create PENDING record
        var create = MakeRequest("ODOO-ORDER-INVALID-001", "PENDING");
        await _client.PostAsJsonAsync("/api/v1/preauth", create);

        // Attempt invalid transition: PENDING → COMPLETED (not allowed directly)
        var invalid = MakeRequest("ODOO-ORDER-INVALID-001", "COMPLETED");
        var response = await _client.PostAsJsonAsync("/api/v1/preauth", invalid);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ForwardPreAuth_TerminalStatusAllowsReRequest()
    {
        SetAuthHeader();

        // Create FAILED record directly (Edge reports failed auth)
        var create = MakeRequest("ODOO-ORDER-TERM-001", "FAILED");
        var first = await _client.PostAsJsonAsync("/api/v1/preauth", create);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // Re-request with PENDING → new record created (terminal status allows re-request)
        // The partial unique index ix_preauth_idemp only covers non-terminal statuses,
        // so a new record can be inserted when the existing one is terminal.
        var reRequest = MakeRequest("ODOO-ORDER-TERM-001", "PENDING");
        var second = await _client.PostAsJsonAsync("/api/v1/preauth", reRequest);
        second.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("PENDING");

        // Verify two records exist with same odooOrderId
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var count = await db.PreAuthRecords.IgnoreQueryFilters()
            .CountAsync(p => p.OdooOrderId == "ODOO-ORDER-TERM-001" && p.SiteCode == TestSiteCode);
        count.Should().Be(2);
    }

    [Fact]
    public async Task ForwardPreAuth_WithoutToken_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var request = MakeRequest("ODOO-ORDER-NOAUTH-001", "PENDING");
        var response = await _client.PostAsJsonAsync("/api/v1/preauth", request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ForwardPreAuth_SiteMismatch_Returns400()
    {
        SetAuthHeader();

        var request = new
        {
            siteCode       = "WRONG-SITE-999",
            odooOrderId    = "ODOO-ORDER-MISMATCH-001",
            pumpNumber     = 1,
            nozzleNumber   = 1,
            productCode    = "PMS",
            requestedAmount = 500_00L,
            unitPrice      = 800_00L,
            currency       = "GHS",
            status         = "PENDING",
            requestedAt    = "2026-03-11T08:00:00Z",
            expiresAt      = "2026-03-11T08:30:00Z"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/preauth", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("VALIDATION.SITE_MISMATCH");
    }

    [Fact]
    public async Task ForwardPreAuth_InvalidStatus_Returns400()
    {
        SetAuthHeader();

        var request = MakeRequest("ODOO-ORDER-BADSTATUS-001", "INVALID_STATUS");
        var response = await _client.PostAsJsonAsync("/api/v1/preauth", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("VALIDATION.INVALID_STATUS");
    }

    [Fact]
    public async Task ForwardPreAuth_ZeroAmount_Returns400()
    {
        SetAuthHeader();

        var request = new
        {
            siteCode       = TestSiteCode,
            odooOrderId    = "ODOO-ORDER-ZERO-001",
            pumpNumber     = 1,
            nozzleNumber   = 1,
            productCode    = "PMS",
            requestedAmount = 0L,
            unitPrice      = 800_00L,
            currency       = "GHS",
            status         = "PENDING",
            requestedAt    = "2026-03-11T08:00:00Z",
            expiresAt      = "2026-03-11T08:30:00Z"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/preauth", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("VALIDATION.INVALID_AMOUNT");
    }

    [Fact]
    public async Task ForwardPreAuth_OutboxEventCreated()
    {
        SetAuthHeader();

        var request = MakeRequest("ODOO-ORDER-EVENT-001", "PENDING");
        var response = await _client.PostAsJsonAsync("/api/v1/preauth", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var outbox = await db.OutboxMessages
            .Where(m => m.EventType == "PreAuthCreated")
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        outbox.Should().NotBeNull();
        outbox!.Payload.Should().Contain("PreAuthCreated");
    }

    [Fact]
    public async Task UpdatePreAuthStatus_Dispensing_Returns200AndPublishesEvent()
    {
        SetAuthHeader();

        var create = MakeRequest(
            "ODOO-ORDER-PATCH-001",
            "AUTHORIZED",
            fccCorrelationId: "CORR-PATCH-001",
            fccAuthorizationCode: "AUTH-PATCH-001");

        var createResponse = await _client.PostAsJsonAsync("/api/v1/preauth", create);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var preAuthId = created.GetProperty("id").GetGuid();

        var patchResponse = await _client.PatchAsJsonAsync($"/api/v1/preauth/{preAuthId}", new
        {
            status = "DISPENSING",
            fccCorrelationId = "CORR-PATCH-001"
        });

        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var record = await db.PreAuthRecords.IgnoreQueryFilters()
            .FirstAsync(p => p.Id == preAuthId);

        record.Status.Should().Be(PreAuthStatus.DISPENSING);
        record.DispensingAt.Should().NotBeNull();

        var outbox = await db.OutboxMessages
            .Where(m => m.EventType == "PreAuthDispensing")
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        outbox.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdatePreAuthStatus_InvalidTransition_Returns409()
    {
        SetAuthHeader();

        var create = MakeRequest("ODOO-ORDER-PATCH-INVALID-001", "PENDING");
        var createResponse = await _client.PostAsJsonAsync("/api/v1/preauth", create);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var preAuthId = created.GetProperty("id").GetGuid();

        var patchResponse = await _client.PatchAsJsonAsync($"/api/v1/preauth/{preAuthId}", new
        {
            status = "COMPLETED"
        });

        patchResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetAuthHeader()
    {
        var token = CreateDeviceJwt(TestDeviceId, TestSiteCode, TestLegalEntityId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static object MakeRequest(
        string odooOrderId,
        string status,
        string? fccCorrelationId = null,
        string? fccAuthorizationCode = null) => new
    {
        siteCode       = TestSiteCode,
        odooOrderId,
        pumpNumber     = 1,
        nozzleNumber   = 1,
        productCode    = "PMS",
        requestedAmount = 500_00L,
        unitPrice      = 800_00L,
        currency       = "GHS",
        status,
        requestedAt    = "2026-03-11T08:00:00Z",
        expiresAt      = "2026-03-11T08:30:00Z",
        fccCorrelationId,
        fccAuthorizationCode
    };

    private static string CreateDeviceJwt(
        string deviceId,
        string siteCode,
        Guid legalEntityId,
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
            issuer:             TestIssuer,
            audience:           TestAudience,
            claims:             claims,
            notBefore:          now.UtcDateTime,
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
            CountryCode           = "GH",
            Name                  = "PreAuth Test Ghana Ltd",
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
            Id                = TestSiteId,
            LegalEntityId     = TestLegalEntityId,
            SiteCode          = TestSiteCode,
            SiteName          = "PreAuth Integration Test Station",
            OperatingModel    = SiteOperatingModel.COCO,
            CompanyTaxPayerId = "TAX-PREAUTH-001",
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
            CredentialRef      = "test-cred-preauth",
            IngestionMethod    = IngestionMethod.PUSH,
            IngestionMode      = IngestionMode.CLOUD_DIRECT,
            IsActive           = true,
            ConfigVersion      = 1,
            CreatedAt          = DateTimeOffset.UtcNow,
            UpdatedAt          = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }
}

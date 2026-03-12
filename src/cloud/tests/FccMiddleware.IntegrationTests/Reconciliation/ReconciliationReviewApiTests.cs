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

namespace FccMiddleware.IntegrationTests.Reconciliation;

[Collection("Integration")]
public sealed class ReconciliationReviewApiTests : IAsyncLifetime
{
    private const string TestDeviceSigningKey = "TestSigningKey-Device-Integration-256bits!!!!!";
    private const string TestDeviceIssuer = "fcc-middleware-cloud";
    private const string TestDeviceAudience = "fcc-middleware-api";
    private const string TestPortalSigningKey = "TestSigningKey-Portal-Integration-256bits!!!!!";
    private const string TestPortalIssuer = "https://login.microsoftonline.com/test-tenant-id/v2.0";
    private const string TestPortalAudience = "00000000-0000-0000-0000-000000000123";

    private static readonly Guid ScopedLegalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000051");
    private static readonly Guid OtherLegalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000052");
    private static readonly Guid FlaggedReconciliationId = Guid.Parse("99000000-0000-0000-0000-000000000061");
    private static readonly Guid UnmatchedReconciliationId = Guid.Parse("99000000-0000-0000-0000-000000000062");
    private static readonly Guid ApprovedReconciliationId = Guid.Parse("99000000-0000-0000-0000-000000000063");
    private static readonly Guid CrossTenantReconciliationId = Guid.Parse("99000000-0000-0000-0000-000000000064");
    private static readonly Guid FlaggedTransactionId = Guid.Parse("99000000-0000-0000-0000-000000000071");
    private static readonly Guid UnmatchedTransactionId = Guid.Parse("99000000-0000-0000-0000-000000000072");
    private static readonly Guid ApprovedTransactionId = Guid.Parse("99000000-0000-0000-0000-000000000073");
    private static readonly Guid CrossTenantTransactionId = Guid.Parse("99000000-0000-0000-0000-000000000074");

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
                        ["DeviceJwt:SigningKey"] = TestDeviceSigningKey,
                        ["DeviceJwt:Issuer"] = TestDeviceIssuer,
                        ["DeviceJwt:Audience"] = TestDeviceAudience,
                        ["PortalJwt:SigningKey"] = TestPortalSigningKey,
                        ["PortalJwt:Authority"] = TestPortalIssuer,
                        ["PortalJwt:Audience"] = TestPortalAudience,
                        ["PortalJwt:ClientId"] = TestPortalAudience
                    });
                });
            });

        _ = _factory.Server;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.StopAsync(), _redis.StopAsync());
    }

    [Fact]
    public async Task GetExceptions_ReturnsScopedFlaggedAndUnmatchedRecords()
    {
        SetPortalAuth("OperationsManager", "portal-user-1", ScopedLegalEntityId);

        var response = await _client.GetAsync(
            $"/api/v1/ops/reconciliation/exceptions?legalEntityId={ScopedLegalEntityId}&siteCode=OPS-SITE-001");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("data").EnumerateArray().ToList();

        items.Should().HaveCount(2);
        items.Select(x => x.GetProperty("reconciliationId").GetGuid())
            .Should().BeEquivalentTo([FlaggedReconciliationId, UnmatchedReconciliationId]);
        items.Select(x => x.GetProperty("status").GetString())
            .Should().BeEquivalentTo(["VARIANCE_FLAGGED", "UNMATCHED"]);
    }

    [Fact]
    public async Task GetExceptions_WithDeviceToken_ReturnsUnauthorized()
    {
        SetDeviceAuth("OPS-SITE-001", ScopedLegalEntityId);

        var response = await _client.GetAsync(
            $"/api/v1/ops/reconciliation/exceptions?legalEntityId={ScopedLegalEntityId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetExceptions_WithStatusFilter_OnlyReturnsRequestedStatus()
    {
        SetPortalAuth("OperationsManager", "portal-user-1", ScopedLegalEntityId);

        var response = await _client.GetAsync(
            $"/api/v1/ops/reconciliation/exceptions?legalEntityId={ScopedLegalEntityId}&status=APPROVED");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("data").EnumerateArray().ToList();

        items.Should().ContainSingle();
        items[0].GetProperty("reconciliationId").GetGuid().Should().Be(ApprovedReconciliationId);
        items[0].GetProperty("status").GetString().Should().Be("APPROVED");
    }

    [Fact]
    public async Task Approve_UpdatesReviewFieldsAndPublishesEvent()
    {
        SetPortalAuth("OperationsManager", "portal-user-approve", ScopedLegalEntityId);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/ops/reconciliation/{FlaggedReconciliationId}/approve",
            new { reason = "Variance validated against till closeout." });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

        var record = await db.ReconciliationRecords.IgnoreQueryFilters()
            .FirstAsync(r => r.Id == FlaggedReconciliationId);
        record.Status.Should().Be(ReconciliationStatus.APPROVED);
        record.ReviewedByUserId.Should().Be("portal-user-approve");
        record.ReviewReason.Should().Be("Variance validated against till closeout.");
        record.ReviewedAtUtc.Should().NotBeNull();

        var eventTypes = await db.OutboxMessages
            .Where(m => m.CorrelationId == Guid.Parse("11111111-1111-1111-1111-111111111111"))
            .Select(m => m.EventType)
            .ToListAsync();

        eventTypes.Should().Contain("ReconciliationApproved");
    }

    [Fact]
    public async Task Reject_RequiresAllowedRole()
    {
        SetPortalAuth("Auditor", "portal-auditor", ScopedLegalEntityId);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/ops/reconciliation/{FlaggedReconciliationId}/reject",
            new { reason = "Should not be allowed." });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reject_RequiresReason()
    {
        SetPortalAuth("SystemAdmin", "portal-admin", ScopedLegalEntityId);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/ops/reconciliation/{FlaggedReconciliationId}/reject",
            new { reason = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("VALIDATION.REASON_REQUIRED");
    }

    [Fact]
    public async Task Approve_RejectsCrossTenantReview()
    {
        SetPortalAuth("OperationsManager", "portal-user-1", ScopedLegalEntityId);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/ops/reconciliation/{CrossTenantReconciliationId}/approve",
            new { reason = "Not in my scope." });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private void SetPortalAuth(string role, string oid, params Guid[] legalEntityIds)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(TestPortalSigningKey);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, oid),
            new("oid", oid),
            new("roles", role)
        };

        if (legalEntityIds.Length == 0 && role.Equals("SystemAdmin", StringComparison.OrdinalIgnoreCase))
        {
            claims.Add(new Claim("legal_entities", "*"));
        }
        else
        {
            claims.Add(new Claim("legal_entities", string.Join(",", legalEntityIds)));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(30),
            Issuer = TestPortalIssuer,
            Audience = TestPortalAudience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(descriptor);
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenHandler.WriteToken(token));
    }

    private void SetDeviceAuth(string siteCode, Guid legalEntityId)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(TestDeviceSigningKey);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, "device-review-test"),
                new Claim("site", siteCode),
                new Claim("lei", legalEntityId.ToString()),
                new Claim("roles", "OperationsManager")
            ]),
            Expires = DateTime.UtcNow.AddMinutes(30),
            Issuer = TestDeviceIssuer,
            Audience = TestDeviceAudience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(descriptor);
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenHandler.WriteToken(token));
    }

    private static async Task SeedAsync(FccMiddlewareDbContext db)
    {
        var now = DateTimeOffset.Parse("2026-03-11T10:00:00Z");

        db.LegalEntities.AddRange(
            new LegalEntity
            {
                Id = ScopedLegalEntityId,
                BusinessCode = "GH-001",
                CountryCode = "GH",
                CountryName = "Ghana",
                Name = "Scoped Entity",
                CurrencyCode = "GHS",
                TaxAuthorityCode = "GRA",
                FiscalizationRequired = false,
                DefaultTimezone = "Africa/Accra",
                OdooCompanyId = "ODOO-GH-001",
                IsActive = true,
                SyncedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            },
            new LegalEntity
            {
                Id = OtherLegalEntityId,
                BusinessCode = "NG-001",
                CountryCode = "NG",
                CountryName = "Nigeria",
                Name = "Other Entity",
                CurrencyCode = "NGN",
                TaxAuthorityCode = "FIRS",
                FiscalizationRequired = false,
                DefaultTimezone = "Africa/Lagos",
                OdooCompanyId = "ODOO-NG-001",
                IsActive = true,
                SyncedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });

        db.Sites.AddRange(
            new Site
            {
                Id = Guid.Parse("99000000-0000-0000-0000-000000000081"),
                LegalEntityId = ScopedLegalEntityId,
                SiteCode = "OPS-SITE-001",
                SiteName = "Ops Site 1",
                OperatingModel = SiteOperatingModel.COCO,
                CompanyTaxPayerId = "TAX-OPS-001",
                SiteUsesPreAuth = true,
                IsActive = true,
                SyncedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            },
            new Site
            {
                Id = Guid.Parse("99000000-0000-0000-0000-000000000082"),
                LegalEntityId = OtherLegalEntityId,
                SiteCode = "OPS-SITE-002",
                SiteName = "Ops Site 2",
                OperatingModel = SiteOperatingModel.COCO,
                CompanyTaxPayerId = "TAX-OPS-002",
                SiteUsesPreAuth = true,
                IsActive = true,
                SyncedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });

        db.Transactions.AddRange(
            MakeTransaction(FlaggedTransactionId, ScopedLegalEntityId, "OPS-SITE-001", Guid.Parse("11111111-1111-1111-1111-111111111111"), now.AddMinutes(-30)),
            MakeTransaction(UnmatchedTransactionId, ScopedLegalEntityId, "OPS-SITE-001", Guid.Parse("22222222-2222-2222-2222-222222222222"), now.AddMinutes(-20)),
            MakeTransaction(ApprovedTransactionId, ScopedLegalEntityId, "OPS-SITE-001", Guid.Parse("33333333-3333-3333-3333-333333333333"), now.AddMinutes(-10)),
            MakeTransaction(CrossTenantTransactionId, OtherLegalEntityId, "OPS-SITE-002", Guid.Parse("44444444-4444-4444-4444-444444444444"), now.AddMinutes(-5)));

        db.ReconciliationRecords.AddRange(
            new ReconciliationRecord
            {
                Id = FlaggedReconciliationId,
                LegalEntityId = ScopedLegalEntityId,
                SiteCode = "OPS-SITE-001",
                TransactionId = FlaggedTransactionId,
                OdooOrderId = "POS/2026/1001",
                PumpNumber = 1,
                NozzleNumber = 1,
                AuthorizedAmountMinorUnits = 10000,
                ActualAmountMinorUnits = 12000,
                VarianceMinorUnits = 2000,
                AbsoluteVarianceMinorUnits = 2000,
                VariancePercent = 20.0m,
                WithinTolerance = false,
                MatchMethod = "CORRELATION_ID",
                Status = ReconciliationStatus.VARIANCE_FLAGGED,
                AmbiguityFlag = false,
                LastMatchAttemptAt = now.AddMinutes(-29),
                CreatedAt = now.AddMinutes(-30),
                UpdatedAt = now.AddMinutes(-30)
            },
            new ReconciliationRecord
            {
                Id = UnmatchedReconciliationId,
                LegalEntityId = ScopedLegalEntityId,
                SiteCode = "OPS-SITE-001",
                TransactionId = UnmatchedTransactionId,
                OdooOrderId = "POS/2026/1002",
                PumpNumber = 2,
                NozzleNumber = 1,
                ActualAmountMinorUnits = 9000,
                MatchMethod = "NONE",
                Status = ReconciliationStatus.UNMATCHED,
                AmbiguityFlag = false,
                LastMatchAttemptAt = now.AddMinutes(-19),
                CreatedAt = now.AddMinutes(-20),
                UpdatedAt = now.AddMinutes(-20)
            },
            new ReconciliationRecord
            {
                Id = ApprovedReconciliationId,
                LegalEntityId = ScopedLegalEntityId,
                SiteCode = "OPS-SITE-001",
                TransactionId = ApprovedTransactionId,
                OdooOrderId = "POS/2026/1003",
                PumpNumber = 3,
                NozzleNumber = 1,
                AuthorizedAmountMinorUnits = 10000,
                ActualAmountMinorUnits = 10100,
                VarianceMinorUnits = 100,
                AbsoluteVarianceMinorUnits = 100,
                VariancePercent = 1.0m,
                WithinTolerance = true,
                MatchMethod = "CORRELATION_ID",
                Status = ReconciliationStatus.APPROVED,
                AmbiguityFlag = false,
                ReviewedByUserId = "existing-reviewer",
                ReviewedAtUtc = now.AddMinutes(-9),
                ReviewReason = "Previously reviewed.",
                LastMatchAttemptAt = now.AddMinutes(-9),
                CreatedAt = now.AddMinutes(-10),
                UpdatedAt = now.AddMinutes(-9)
            },
            new ReconciliationRecord
            {
                Id = CrossTenantReconciliationId,
                LegalEntityId = OtherLegalEntityId,
                SiteCode = "OPS-SITE-002",
                TransactionId = CrossTenantTransactionId,
                OdooOrderId = "POS/2026/1004",
                PumpNumber = 4,
                NozzleNumber = 1,
                AuthorizedAmountMinorUnits = 10000,
                ActualAmountMinorUnits = 13000,
                VarianceMinorUnits = 3000,
                AbsoluteVarianceMinorUnits = 3000,
                VariancePercent = 30.0m,
                WithinTolerance = false,
                MatchMethod = "CORRELATION_ID",
                Status = ReconciliationStatus.VARIANCE_FLAGGED,
                AmbiguityFlag = false,
                LastMatchAttemptAt = now.AddMinutes(-4),
                CreatedAt = now.AddMinutes(-5),
                UpdatedAt = now.AddMinutes(-5)
            });

        await db.SaveChangesAsync();
    }

    private static Transaction MakeTransaction(
        Guid id,
        Guid legalEntityId,
        string siteCode,
        Guid correlationId,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = id,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LegalEntityId = legalEntityId,
            FccTransactionId = $"TX-{id.ToString()[..8]}",
            SiteCode = siteCode,
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "PMS",
            VolumeMicrolitres = 1_000_000,
            AmountMinorUnits = 10_000,
            UnitPriceMinorPerLitre = 10_000,
            CurrencyCode = "GHS",
            StartedAt = createdAt.AddMinutes(-2),
            CompletedAt = createdAt.AddMinutes(-1),
            FccVendor = FccVendor.DOMS,
            Status = TransactionStatus.PENDING,
            IngestionSource = IngestionSource.FCC_PUSH,
            CorrelationId = correlationId
        };
}

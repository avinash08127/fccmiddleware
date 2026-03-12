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
public sealed class ReconciliationMatchingTests : IAsyncLifetime
{
    private const string TestSigningKey = "TestSigningKey-EdgeAgent-Integration-256bits!!";
    private const string TestIssuer = "fcc-middleware-cloud";
    private const string TestAudience = "fcc-middleware-api";

    private static readonly Guid TestLegalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000041");
    private static readonly Guid TestSiteId = Guid.Parse("99000000-0000-0000-0000-000000000042");
    private static readonly Guid TestFccConfigId = Guid.Parse("99000000-0000-0000-0000-000000000043");

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
                        ["Reconciliation:DefaultAmountTolerancePercent"] = "2.0",
                        ["Reconciliation:DefaultAmountToleranceAbsolute"] = "100",
                        ["Reconciliation:DefaultTimeWindowMinutes"] = "15"
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
    public async Task PreAuthThenDispense_IsReconciledSynchronouslyAsMatched()
    {
        SetDeviceAuthHeader("RECON-SITE-001", TestLegalEntityId);

        var preAuthResponse = await _client.PostAsJsonAsync("/api/v1/preauth", new
        {
            siteCode = "RECON-SITE-001",
            odooOrderId = "POS/2026/00077",
            pumpNumber = 4,
            nozzleNumber = 2,
            productCode = "PMS",
            requestedAmount = 3687000L,
            unitPrice = 81500L,
            currency = "GHS",
            status = "AUTHORIZED",
            requestedAt = "2026-03-11T13:40:00Z",
            expiresAt = "2026-03-11T14:10:00Z",
            fccCorrelationId = "RECON-CORR-001",
            fccAuthorizationCode = "AUTH-001"
        });

        preAuthResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        _client.DefaultRequestHeaders.Authorization = null;

        var ingestResponse = await _client.PostAsJsonAsync("/api/v1/transactions/ingest", new
        {
            fccVendor = "DOMS",
            siteCode = "RECON-SITE-001",
            capturedAt = "2026-03-11T13:55:00Z",
            rawPayload = new
            {
                transactionId = "TXN-RECON-001",
                pumpNumber = 4,
                nozzleNumber = 2,
                productCode = "PMS",
                volumeMicrolitres = 45_230_000L,
                amountMinorUnits = 3_687_000L,
                unitPriceMinorPerLitre = 81_500L,
                startTime = "2026-03-11T13:50:00Z",
                endTime = "2026-03-11T13:54:50Z",
                fccCorrelationId = "RECON-CORR-001",
                odooOrderId = "POS/2026/00077"
            }
        });

        ingestResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var ingestBody = await ingestResponse.Content.ReadFromJsonAsync<JsonElement>();
        var transactionId = ingestBody.GetProperty("transactionId").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

        var transaction = await db.Transactions.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == transactionId);
        var preAuth = await db.PreAuthRecords.IgnoreQueryFilters()
            .FirstAsync(p => p.OdooOrderId == "POS/2026/00077");
        var reconciliation = await db.ReconciliationRecords.IgnoreQueryFilters()
            .FirstAsync(r => r.TransactionId == transactionId);

        transaction.PreAuthId.Should().Be(preAuth.Id);
        transaction.ReconciliationStatus.Should().Be(ReconciliationStatus.MATCHED);
        preAuth.Status.Should().Be(PreAuthStatus.COMPLETED);
        preAuth.MatchedTransactionId.Should().Be(transactionId);
        reconciliation.Status.Should().Be(ReconciliationStatus.MATCHED);
        reconciliation.MatchMethod.Should().Be("CORRELATION_ID");

        var eventTypes = await db.OutboxMessages
            .Where(m => m.CorrelationId == transaction.CorrelationId)
            .Select(m => m.EventType)
            .ToListAsync();

        eventTypes.Should().Contain("ReconciliationMatched");
    }

    private void SetDeviceAuthHeader(string siteCode, Guid legalEntityId)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(TestSigningKey);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, "device-reconciliation-test"),
                new Claim(ClaimTypes.NameIdentifier, "device-reconciliation-test"),
                new Claim("site", siteCode),
                new Claim("lei", legalEntityId.ToString())
            ]),
            Expires = DateTime.UtcNow.AddMinutes(30),
            Issuer = TestIssuer,
            Audience = TestAudience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(descriptor);
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenHandler.WriteToken(token));
    }

    private static async Task SeedTestDataAsync(FccMiddlewareDbContext db)
    {
        db.LegalEntities.Add(new LegalEntity
        {
            Id = TestLegalEntityId,
            BusinessCode = "GH-001",
            CountryCode = "GH",
            CountryName = "Ghana",
            Name = "Recon Ghana Ltd",
            CurrencyCode = "GHS",
            TaxAuthorityCode = "GRA",
            FiscalizationRequired = false,
            DefaultTimezone = "Africa/Accra",
            OdooCompanyId = "ODOO-GH-001",
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AmountTolerancePercent = 2.0m,
            AmountToleranceAbsolute = 100,
            TimeWindowMinutes = 15
        });

        db.Sites.Add(new Site
        {
            Id = TestSiteId,
            LegalEntityId = TestLegalEntityId,
            SiteCode = "RECON-SITE-001",
            SiteName = "Reconciliation Test Station",
            OperatingModel = SiteOperatingModel.COCO,
            CompanyTaxPayerId = "TAX-RECON-001",
            SiteUsesPreAuth = true,
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.FccConfigs.Add(new FccConfig
        {
            Id = TestFccConfigId,
            SiteId = TestSiteId,
            LegalEntityId = TestLegalEntityId,
            FccVendor = FccVendor.DOMS,
            ConnectionProtocol = ConnectionProtocol.REST,
            HostAddress = "127.0.0.1",
            Port = 8080,
            CredentialRef = "test-cred",
            IngestionMethod = IngestionMethod.PUSH,
            IngestionMode = IngestionMode.CLOUD_DIRECT,
            IsActive = true,
            ConfigVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }
}

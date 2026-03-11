using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Api.Auth;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FccMiddleware.IntegrationTests.Ingestion;

/// <summary>
/// Integration tests for POST /api/v1/transactions/ingest.
/// Spins up real PostgreSQL and Redis containers and applies the EF Core schema.
/// </summary>
[Collection("Integration")]
public sealed class IngestionTests : IAsyncLifetime
{
    private const string TestFccApiKeyId = "fcc-client-001";
    private const string TestFccSecret = "fcc-secret-integration-key-32-chars";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine").Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    // Canonical DOMS payload that passes all validation
    private static readonly object ValidDomsPayload = new
    {
        transactionId = "TXN-INTEG-001",
        pumpNumber = 3,
        nozzleNumber = 1,
        productCode = "PMS",
        volumeMicrolitres = 45_230_000L,
        amountMinorUnits = 36_870_00L,
        unitPriceMinorPerLitre = 815_00L,
        startTime = "2026-03-11T13:50:00Z",
        endTime   = "2026-03-11T13:54:50Z"
    };

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
                        ["FccHmac:Clients:0:ApiKeyId"]      = TestFccApiKeyId,
                        ["FccHmac:Clients:0:Secret"]        = TestFccSecret,
                        ["FccHmac:Clients:0:SiteCode"]      = "ACCRA-001"
                    });
                });
            });

        // Trigger app startup so services are registered
        _ = _factory.Server;

        // Apply EF Core schema and seed test data
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
    public async Task Ingest_ValidTransaction_Returns202AndStoredAsPending()
    {
        var request = new
        {
            fccVendor = "DOMS",
            siteCode = "ACCRA-001",
            capturedAt = "2026-03-11T13:55:00Z",
            rawPayload = ValidDomsPayload
        };

        var response = await PostIngestAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("PENDING");
        var transactionId = body.GetProperty("transactionId").GetGuid();
        transactionId.Should().NotBeEmpty();

        // Verify stored in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var stored = await db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == transactionId);

        stored.Should().NotBeNull();
        stored!.Status.Should().Be(TransactionStatus.PENDING);
        stored.FccTransactionId.Should().Be("TXN-INTEG-001");
        stored.SiteCode.Should().Be("ACCRA-001");

        // Verify TransactionIngested outbox event was written
        var outbox = await db.OutboxMessages
            .Where(m => m.EventType == "TransactionIngested" && m.CorrelationId == stored.CorrelationId)
            .FirstOrDefaultAsync();
        outbox.Should().NotBeNull();
    }

    [Fact]
    public async Task Ingest_SameTransactionTwice_SecondReturns409WithOriginalId()
    {
        var request = new
        {
            fccVendor = "DOMS",
            siteCode = "ACCRA-001",
            capturedAt = "2026-03-11T14:00:00Z",
            rawPayload = new
            {
                transactionId = "TXN-INTEG-DEDUP",
                pumpNumber = 2,
                nozzleNumber = 1,
                productCode = "AGO",
                volumeMicrolitres = 20_000_000L,
                amountMinorUnits = 18_000_00L,
                unitPriceMinorPerLitre = 900_00L,
                startTime = "2026-03-11T14:00:00Z",
                endTime   = "2026-03-11T14:05:00Z"
            }
        };

        // First ingest — should succeed
        var first = await PostIngestAsync(request);
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var originalId = firstBody.GetProperty("transactionId").GetGuid();

        // Second ingest — same (fccTransactionId, siteCode) → should conflict
        var second = await PostIngestAsync(request);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var errorBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        errorBody.GetProperty("errorCode").GetString().Should().Be("CONFLICT.DUPLICATE_TRANSACTION");
        var details = errorBody.GetProperty("details");
        details.GetProperty("existingId").GetGuid().Should().Be(originalId);
    }

    [Fact]
    public async Task Ingest_InvalidPayload_Returns400WithStructuredError()
    {
        var request = new
        {
            fccVendor = "DOMS",
            siteCode = "ACCRA-001",
            capturedAt = "2026-03-11T14:10:00Z",
            rawPayload = new
            {
                // Missing transactionId — DOMS validation will fail
                pumpNumber = 1,
                nozzleNumber = 1,
                productCode = "PMS",
                volumeMicrolitres = 10_000_000L,
                amountMinorUnits = 8_000_00L,
                unitPriceMinorPerLitre = 800_00L,
                startTime = "2026-03-11T14:10:00Z",
                endTime   = "2026-03-11T14:12:00Z"
            }
        };

        var response = await PostIngestAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().StartWith("VALIDATION.");
        body.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("timestamp").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Ingest_InvalidHmacSignature_Returns401()
    {
        var request = new
        {
            fccVendor = "DOMS",
            siteCode = "ACCRA-001",
            capturedAt = "2026-03-11T14:10:00Z",
            rawPayload = ValidDomsPayload
        };

        var response = await PostIngestAsync(request, useValidSignature: false);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ingest_ExpiredHmacTimestamp_Returns401()
    {
        var request = new
        {
            fccVendor = "DOMS",
            siteCode = "ACCRA-001",
            capturedAt = "2026-03-11T14:10:00Z",
            rawPayload = ValidDomsPayload
        };

        var response = await PostIngestAsync(request, timestamp: DateTimeOffset.UtcNow.AddMinutes(-10));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PostIngestAsync(
        object request,
        bool useValidSignature = true,
        DateTimeOffset? timestamp = null)
    {
        var body = JsonSerializer.Serialize(request);
        var sentAt = timestamp ?? DateTimeOffset.UtcNow;
        var timestampValue = sentAt.ToString("O");
        var signature = useValidSignature
            ? ComputeSignature("POST", "/api/v1/transactions/ingest", timestampValue, body)
            : "bad-signature";

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions/ingest")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        message.Headers.Add(FccHmacAuthOptions.ApiKeyHeaderName, TestFccApiKeyId);
        message.Headers.Add(FccHmacAuthOptions.SignatureHeaderName, signature);
        message.Headers.Add(FccHmacAuthOptions.TimestampHeaderName, timestampValue);

        return await _client.SendAsync(message);
    }

    private static string ComputeSignature(string method, string path, string timestamp, string body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(TestFccSecret));
        var canonical = $"{method}{path}{timestamp}{bodyHash}";
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static async Task SeedTestDataAsync(FccMiddlewareDbContext db)
    {
        var legalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000001");
        if (await db.LegalEntities.IgnoreQueryFilters().AnyAsync(e => e.Id == legalEntityId)) return;

        var siteId        = Guid.Parse("99000000-0000-0000-0000-000000000002");
        var fccConfigId   = Guid.Parse("99000000-0000-0000-0000-000000000003");

        db.LegalEntities.Add(new LegalEntity
        {
            Id = legalEntityId,
            CountryCode = "GH",
            Name = "Test Ghana Ltd",
            CurrencyCode = "GHS",
            TaxAuthorityCode = "GRA",
            FiscalizationRequired = false,
            DefaultTimezone = "Africa/Accra",
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.Sites.Add(new Site
        {
            Id = siteId,
            LegalEntityId = legalEntityId,
            SiteCode = "ACCRA-001",
            SiteName = "Accra Test Station",
            OperatingModel = SiteOperatingModel.COCO,
            CompanyTaxPayerId = "TAX-001",
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.FccConfigs.Add(new FccConfig
        {
            Id = fccConfigId,
            SiteId = siteId,
            LegalEntityId = legalEntityId,
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

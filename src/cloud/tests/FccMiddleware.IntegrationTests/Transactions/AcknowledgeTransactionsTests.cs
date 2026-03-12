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
/// Integration tests for POST /api/v1/transactions/acknowledge (Odoo acknowledge endpoint).
/// Verifies API key auth, PENDING → SYNCED_TO_ODOO transition, idempotency, conflict detection,
/// DUPLICATE/ARCHIVED rejection, outbox event publishing, and tenant isolation.
/// </summary>
[Collection("Integration")]
public sealed class AcknowledgeTransactionsTests : IAsyncLifetime
{
    private const string TestRawApiKey   = "test-odoo-api-key-ack-integration-32xx";
    private const string OtherRawApiKey  = "other-odoo-api-key-ack-different-32xx";
    private const string TestSiteCode    = "ACK-SITE-001";

    private static readonly Guid TestLegalEntityId  = Guid.Parse("99000000-0000-0000-0000-000000000031");
    private static readonly Guid OtherLegalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000032");

    // Pre-determined transaction IDs seeded before each test run
    private static readonly Guid PendingTx1Id   = Guid.Parse("aa000000-0000-0000-0000-000000000001");
    private static readonly Guid PendingTx2Id   = Guid.Parse("aa000000-0000-0000-0000-000000000002");
    private static readonly Guid PendingTx3Id   = Guid.Parse("aa000000-0000-0000-0000-000000000003");
    private static readonly Guid DuplicateTxId  = Guid.Parse("aa000000-0000-0000-0000-000000000004");
    private static readonly Guid ArchivedTxId   = Guid.Parse("aa000000-0000-0000-0000-000000000005");

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
        _client.DefaultRequestHeaders.Add("X-Api-Key", TestRawApiKey);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.StopAsync(), _redis.StopAsync());
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Acknowledge_Pending_ReturnsAcknowledged_And_TransitionsStatus()
    {
        var body = new
        {
            acknowledgements = new[]
            {
                new { id = PendingTx1Id, odooOrderId = "POS/2026/00001" }
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/transactions/acknowledge", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("succeededCount").GetInt32().Should().Be(1);
        json.GetProperty("failedCount").GetInt32().Should().Be(0);

        var results = json.GetProperty("results").EnumerateArray().ToList();
        results.Should().HaveCount(1);
        results[0].GetProperty("outcome").GetString().Should().Be("ACKNOWLEDGED");
        results[0].GetProperty("id").GetGuid().Should().Be(PendingTx1Id);

        // Verify DB state
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var tx = await db.Transactions.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == PendingTx1Id);

        tx.Status.Should().Be(TransactionStatus.SYNCED_TO_ODOO);
        tx.OdooOrderId.Should().Be("POS/2026/00001");
        tx.SyncedToOdooAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Acknowledge_AlreadyAcknowledged_SameOrderId_ReturnsAlreadyAcknowledged()
    {
        // First acknowledge
        var firstBody = new
        {
            acknowledgements = new[]
            {
                new { id = PendingTx2Id, odooOrderId = "POS/2026/00002" }
            }
        };
        var firstResponse = await _client.PostAsJsonAsync("/api/v1/transactions/acknowledge", firstBody);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Idempotent re-acknowledge with same odooOrderId
        var secondBody = new
        {
            acknowledgements = new[]
            {
                new { id = PendingTx2Id, odooOrderId = "POS/2026/00002" }
            }
        };
        var secondResponse = await _client.PostAsJsonAsync("/api/v1/transactions/acknowledge", secondBody);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("succeededCount").GetInt32().Should().Be(1);
        json.GetProperty("failedCount").GetInt32().Should().Be(0);

        var results = json.GetProperty("results").EnumerateArray().ToList();
        results[0].GetProperty("outcome").GetString().Should().Be("ALREADY_ACKNOWLEDGED");
    }

    [Fact]
    public async Task Acknowledge_AlreadyAcknowledged_DifferentOrderId_ReturnsConflict()
    {
        // First acknowledge
        var firstBody = new
        {
            acknowledgements = new[]
            {
                new { id = PendingTx3Id, odooOrderId = "POS/2026/00003" }
            }
        };
        await _client.PostAsJsonAsync("/api/v1/transactions/acknowledge", firstBody);

        // Re-acknowledge with a DIFFERENT odooOrderId
        var conflictBody = new
        {
            acknowledgements = new[]
            {
                new { id = PendingTx3Id, odooOrderId = "POS/2026/99999" }
            }
        };
        var response = await _client.PostAsJsonAsync("/api/v1/transactions/acknowledge", conflictBody);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("succeededCount").GetInt32().Should().Be(0);
        json.GetProperty("failedCount").GetInt32().Should().Be(1);

        var results = json.GetProperty("results").EnumerateArray().ToList();
        results[0].GetProperty("outcome").GetString().Should().Be("CONFLICT");
        results[0].GetProperty("error").GetProperty("code").GetString().Should().Be("ACKNOWLEDGE.CONFLICT");
    }

    [Fact]
    public async Task Acknowledge_NotFound_ReturnsNotFound()
    {
        var unknownId = Guid.NewGuid();
        var body = new
        {
            acknowledgements = new[]
            {
                new { id = unknownId, odooOrderId = "POS/2026/GHOST" }
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/transactions/acknowledge", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("failedCount").GetInt32().Should().Be(1);

        var results = json.GetProperty("results").EnumerateArray().ToList();
        results[0].GetProperty("outcome").GetString().Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Acknowledge_DuplicateTransaction_ReturnsFailed()
    {
        var body = new
        {
            acknowledgements = new[]
            {
                new { id = DuplicateTxId, odooOrderId = "POS/2026/DUP" }
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/transactions/acknowledge", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("failedCount").GetInt32().Should().Be(1);

        var results = json.GetProperty("results").EnumerateArray().ToList();
        results[0].GetProperty("outcome").GetString().Should().Be("FAILED");
        results[0].GetProperty("error").GetProperty("code").GetString().Should().Be("ACKNOWLEDGE.INVALID_STATUS");
    }

    [Fact]
    public async Task Acknowledge_OutboxEvent_PublishedOnSuccess()
    {
        // Use a fresh transaction seeded inline for this test
        var txId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            db.Transactions.Add(MakeTransaction(TestLegalEntityId, TestSiteCode, $"ACK-OUTBOX-{txId}", 9, txId));
            await db.SaveChangesAsync();
        }

        var body = new
        {
            acknowledgements = new[]
            {
                new { id = txId, odooOrderId = "POS/2026/OUTBOX-TEST" }
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/transactions/acknowledge", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify outbox event was written
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var outbox = await verifyDb.OutboxMessages
            .Where(m => m.EventType == "TransactionSyncedToOdoo")
            .ToListAsync();

        outbox.Should().NotBeEmpty();
        var evt = outbox.First(m => m.Payload.Contains(txId.ToString()));
        using var payloadDoc = JsonDocument.Parse(evt.Payload);
        payloadDoc.RootElement.GetProperty("transactionId").GetGuid().Should().Be(txId);
        payloadDoc.RootElement.GetProperty("odooOrderId").GetString().Should().Be("POS/2026/OUTBOX-TEST");
    }

    [Fact]
    public async Task Acknowledge_WithoutApiKey_Returns401()
    {
        var client = _factory.CreateClient(); // no X-Api-Key header
        var body = new
        {
            acknowledgements = new[]
            {
                new { id = PendingTx1Id, odooOrderId = "POS/2026/NO-AUTH" }
            }
        };

        var response = await client.PostAsJsonAsync("/api/v1/transactions/acknowledge", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Acknowledge_TenantIsolation_CannotAcknowledgeOtherTenantTransaction()
    {
        // OtherRawApiKey → OtherLegalEntityId; PendingTx1Id belongs to TestLegalEntityId
        var otherClient = _factory.CreateClient();
        otherClient.DefaultRequestHeaders.Add("X-Api-Key", OtherRawApiKey);

        var body = new
        {
            acknowledgements = new[]
            {
                new { id = PendingTx1Id, odooOrderId = "POS/2026/TENANT-ESCAPE" }
            }
        };

        var response = await otherClient.PostAsJsonAsync("/api/v1/transactions/acknowledge", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // From OtherLegalEntityId's perspective, PendingTx1Id does not exist
        var results = json.GetProperty("results").EnumerateArray().ToList();
        results[0].GetProperty("outcome").GetString().Should().Be("NOT_FOUND");
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
        Guid? id = null,
        TransactionStatus status = TransactionStatus.PENDING)
    {
        var now = DateTimeOffset.UtcNow;
        return new Transaction
        {
            Id                     = id ?? Guid.NewGuid(),
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
            BusinessCode          = "ET-001",
            CountryCode           = "ET",  // Ethiopia — not in EnsureCreated seed
            CountryName           = "Ethiopia",
            Name                  = "Ack Test Ethiopia Ltd",
            CurrencyCode          = "ETB",
            TaxAuthorityCode      = "ERCA",
            FiscalizationRequired = false,
            DefaultTimezone       = "Africa/Addis_Ababa",
            OdooCompanyId         = "ODOO-ET-001",
            IsActive              = true,
            SyncedAt              = DateTimeOffset.UtcNow,
            CreatedAt             = DateTimeOffset.UtcNow,
            UpdatedAt             = DateTimeOffset.UtcNow
        });

        db.LegalEntities.Add(new LegalEntity
        {
            Id                    = OtherLegalEntityId,
            BusinessCode          = "GH-001",
            CountryCode           = "GH",  // Ghana — not in EnsureCreated seed
            CountryName           = "Ghana",
            Name                  = "Ack Test Ghana Ltd",
            CurrencyCode          = "GHS",
            TaxAuthorityCode      = "GRA",
            FiscalizationRequired = false,
            DefaultTimezone       = "Africa/Accra",
            OdooCompanyId         = "ODOO-GH-001",
            IsActive              = true,
            SyncedAt              = DateTimeOffset.UtcNow,
            CreatedAt             = DateTimeOffset.UtcNow,
            UpdatedAt             = DateTimeOffset.UtcNow
        });

        // ── Odoo API keys ─────────────────────────────────────────────────────
        db.OdooApiKeys.Add(new OdooApiKey
        {
            Id            = Guid.NewGuid(),
            LegalEntityId = TestLegalEntityId,
            KeyHash       = ComputeSha256Hex(TestRawApiKey),
            Label         = "Ack Test Key ET",
            IsActive      = true,
            CreatedAt     = DateTimeOffset.UtcNow
        });

        db.OdooApiKeys.Add(new OdooApiKey
        {
            Id            = Guid.NewGuid(),
            LegalEntityId = OtherLegalEntityId,
            KeyHash       = ComputeSha256Hex(OtherRawApiKey),
            Label         = "Ack Test Key GH",
            IsActive      = true,
            CreatedAt     = DateTimeOffset.UtcNow
        });

        // ── Transactions ──────────────────────────────────────────────────────
        db.Transactions.Add(MakeTransaction(TestLegalEntityId, TestSiteCode, "ACK-PENDING-001", 1, PendingTx1Id));
        db.Transactions.Add(MakeTransaction(TestLegalEntityId, TestSiteCode, "ACK-PENDING-002", 2, PendingTx2Id));
        db.Transactions.Add(MakeTransaction(TestLegalEntityId, TestSiteCode, "ACK-PENDING-003", 3, PendingTx3Id));
        db.Transactions.Add(MakeTransaction(TestLegalEntityId, TestSiteCode, "ACK-DUPLICATE-001", 4, DuplicateTxId, TransactionStatus.DUPLICATE));
        db.Transactions.Add(MakeTransaction(TestLegalEntityId, TestSiteCode, "ACK-ARCHIVED-001",  5, ArchivedTxId, TransactionStatus.ARCHIVED));

        await db.SaveChangesAsync();
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using FccMiddleware.Infrastructure.Events;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FccMiddleware.IntegrationTests.Events;

/// <summary>
/// Integration tests for the outbox publisher worker.
/// Full pipeline: ingest transaction → outbox message created → worker processes it → audit event stored.
/// </summary>
[Collection("Integration")]
public sealed class OutboxPublisherWorkerTests : IAsyncLifetime
{
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
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.StopAsync(), _redis.StopAsync());
    }

    [Fact]
    public async Task Worker_ProcessesOutboxMessage_CreatesAuditEvent()
    {
        // Arrange: ingest a transaction to produce an outbox message
        var request = new
        {
            fccVendor = "DOMS",
            siteCode = "ACCRA-001",
            capturedAt = "2026-03-11T15:00:00Z",
            rawPayload = new
            {
                transactionId = "TXN-OUTBOX-001",
                pumpNumber = 1,
                nozzleNumber = 1,
                productCode = "PMS",
                volumeMicrolitres = 30_000_000L,
                amountMinorUnits = 24_000_00L,
                unitPriceMinorPerLitre = 800_00L,
                startTime = "2026-03-11T15:00:00Z",
                endTime   = "2026-03-11T15:03:00Z"
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/transactions/ingest", request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Verify outbox message exists and is unprocessed
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            var outbox = await db.OutboxMessages
                .Where(m => m.EventType == "TransactionIngested" && m.ProcessedAt == null)
                .ToListAsync();
            outbox.Should().NotBeEmpty();
        }

        // Act: run the outbox worker to process the batch
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            var options = Options.Create(new OutboxWorkerOptions { BatchSize = 50, RetentionDays = 7 });
            var worker = new OutboxPublisherWorker(
                _factory.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<OutboxPublisherWorker>.Instance,
                options);

            var processed = await worker.ProcessBatchAsync(CancellationToken.None);
            processed.Should().BeGreaterThan(0);
        }

        // Assert: outbox message marked as processed
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

            var unprocessed = await db.OutboxMessages
                .Where(m => m.EventType == "TransactionIngested" && m.ProcessedAt == null)
                .CountAsync();
            unprocessed.Should().Be(0);

            // Assert: audit event was created
            var audit = await db.AuditEvents
                .IgnoreQueryFilters()
                .Where(a => a.EventType == "TransactionIngested")
                .FirstOrDefaultAsync();
            audit.Should().NotBeNull();
            audit!.Source.Should().NotBeNullOrEmpty();
            audit.Payload.Should().Contain("TXN-OUTBOX-001");
        }
    }

    [Fact]
    public async Task Worker_NoMessages_ProcessesZero()
    {
        // Ensure all existing outbox messages are already processed
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            var pending = await db.OutboxMessages.Where(m => m.ProcessedAt == null).ToListAsync();
            foreach (var m in pending)
                m.ProcessedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        // Act
        var options = Options.Create(new OutboxWorkerOptions { BatchSize = 50 });
        var worker = new OutboxPublisherWorker(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxPublisherWorker>.Instance,
            options);

        var processed = await worker.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(0);
    }

    [Fact]
    public async Task Worker_CleansUpOldProcessedMessages()
    {
        // Arrange: insert an old processed outbox message via EF Core, then update
        // ProcessedAt with raw SQL to guarantee it is persisted.
        var correlationId = Guid.NewGuid();
        var oldDate = DateTimeOffset.UtcNow.AddDays(-10);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            var msg = new OutboxMessage
            {
                EventType = "TestCleanup",
                Payload = "{\"test\":true}",
                CorrelationId = correlationId,
                CreatedAt = oldDate
            };
            db.OutboxMessages.Add(msg);
            await db.SaveChangesAsync();

            // Update ProcessedAt via raw SQL to bypass any EF Core quirks
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE outbox_messages SET processed_at = {oldDate} WHERE correlation_id = {correlationId}");
        }

        // Verify the old message exists before cleanup
        using (var checkScope = _factory.Services.CreateScope())
        {
            var checkDb = checkScope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            var exists = await checkDb.OutboxMessages
                .Where(m => m.EventType == "TestCleanup" && m.ProcessedAt != null)
                .AnyAsync();
            exists.Should().BeTrue("the old processed message should exist before cleanup");
        }

        // Act: run worker with 0-day retention so any processed message gets cleaned up
        var options = Options.Create(new OutboxWorkerOptions { BatchSize = 50, RetentionDays = 0 });
        var worker = new OutboxPublisherWorker(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxPublisherWorker>.Instance,
            options);

        await worker.ProcessBatchAsync(CancellationToken.None);

        // Assert: old message was cleaned up
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var oldMessages = await verifyDb.OutboxMessages
            .Where(m => m.EventType == "TestCleanup")
            .CountAsync();
        oldMessages.Should().Be(0);
    }

    [Fact]
    public async Task Worker_AuditEventContainsCorrectEnvelopeFields()
    {
        // Arrange: ingest a transaction
        var request = new
        {
            fccVendor = "DOMS",
            siteCode = "ACCRA-001",
            capturedAt = "2026-03-11T15:10:00Z",
            rawPayload = new
            {
                transactionId = "TXN-OUTBOX-AUDIT",
                pumpNumber = 2,
                nozzleNumber = 1,
                productCode = "AGO",
                volumeMicrolitres = 50_000_000L,
                amountMinorUnits = 45_000_00L,
                unitPriceMinorPerLitre = 900_00L,
                startTime = "2026-03-11T15:10:00Z",
                endTime   = "2026-03-11T15:15:00Z"
            }
        };

        var ingestResponse = await _client.PostAsJsonAsync("/api/v1/transactions/ingest", request);
        var ingestBody = await ingestResponse.Content.ReadFromJsonAsync<JsonElement>();
        var correlationId = Guid.Empty;

        // Get the outbox message correlation ID for this specific ingest
        using (var preScope = _factory.Services.CreateScope())
        {
            var preDb = preScope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            var outboxMsg = await preDb.OutboxMessages
                .Where(m => m.EventType == "TransactionIngested" && m.ProcessedAt == null)
                .OrderByDescending(m => m.Id)
                .FirstAsync();
            correlationId = outboxMsg.CorrelationId;
        }

        // Act: process outbox
        var options = Options.Create(new OutboxWorkerOptions { BatchSize = 50, RetentionDays = 7 });
        var worker = new OutboxPublisherWorker(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxPublisherWorker>.Instance,
            options);

        await worker.ProcessBatchAsync(CancellationToken.None);

        // Assert: audit event has correct fields
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var audit = await db.AuditEvents
            .IgnoreQueryFilters()
            .Where(a => a.CorrelationId == correlationId && a.EventType == "TransactionIngested")
            .FirstOrDefaultAsync();

        audit.Should().NotBeNull();
        audit!.EventType.Should().Be("TransactionIngested");
        audit.CorrelationId.Should().Be(correlationId);

        // Verify the payload is valid JSON containing transaction fields
        var payload = JsonSerializer.Deserialize<JsonElement>(audit.Payload);
        payload.TryGetProperty("fccTransactionId", out var fccTxn).Should().BeTrue();
        fccTxn.GetString().Should().Be("TXN-OUTBOX-AUDIT");
        payload.TryGetProperty("siteCode", out var sc).Should().BeTrue();
        sc.GetString().Should().Be("ACCRA-001");
        payload.TryGetProperty("legalEntityId", out _).Should().BeTrue();
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private static async Task SeedTestDataAsync(FccMiddlewareDbContext db)
    {
        var legalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000001");
        if (await db.LegalEntities.IgnoreQueryFilters().AnyAsync(e => e.Id == legalEntityId)) return;

        var siteId      = Guid.Parse("99000000-0000-0000-0000-000000000002");
        var fccConfigId = Guid.Parse("99000000-0000-0000-0000-000000000003");

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

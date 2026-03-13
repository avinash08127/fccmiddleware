using System.Net;
using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Sync;
using FccDesktopAgent.Core.Sync.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Sync;

/// <summary>
/// Unit tests for <see cref="StatusPollWorker"/>.
/// Uses real in-memory SQLite for buffer and SyncState; NSubstitute for HTTP / token provider.
/// </summary>
public sealed class StatusPollWorkerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AgentDbContext> _dbOptions;
    private readonly AgentDbContext _db;
    private readonly IServiceProvider _serviceProvider;
    private readonly IDeviceTokenProvider _tokenProvider;

    public StatusPollWorkerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentDbContext(_dbOptions);
        _db.Database.EnsureCreated();

        _tokenProvider = Substitute.For<IDeviceTokenProvider>();
        _tokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("test-jwt-token"));

        var services = new ServiceCollection();
        services.AddScoped<AgentDbContext>(_ => new AgentDbContext(_dbOptions));
        services.AddScoped<TransactionBufferManager>();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private StatusPollWorker CreateWorker(HttpMessageHandler httpHandler)
    {
        var factory = new TestHttpClientFactory(httpHandler);
        var config = Options.Create(new AgentConfiguration
        {
            CloudBaseUrl = "http://cloud.test",
        });

        var registrationManager = Substitute.For<IRegistrationManager>();
        var authHandler = new AuthenticatedCloudRequestHandler(
            _tokenProvider, registrationManager,
            NullLogger<AuthenticatedCloudRequestHandler>.Instance);

        return new StatusPollWorker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            factory,
            config,
            authHandler,
            registrationManager,
            NullLogger<StatusPollWorker>.Instance);
    }

    private async Task<BufferedTransaction> SeedUploadedTransactionAsync(string fccId = "FCC-001")
    {
        var tx = new BufferedTransaction
        {
            Id = Guid.NewGuid().ToString(),
            FccTransactionId = fccId,
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            VolumeMicrolitres = 50_000_000,
            AmountMinorUnits = 75_000,
            UnitPriceMinorPerLitre = 1500,
            CurrencyCode = "ETB",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            FccVendor = "DOMS",
            IngestionSource = "EDGE_POLL",
            Status = TransactionStatus.Synced,
            SyncStatus = SyncStatus.Uploaded,
        };
        _db.Transactions.Add(tx);
        await _db.SaveChangesAsync();
        return tx;
    }

    private static HttpMessageHandler RespondWithSyncedIds(params string[] fccIds)
    {
        var response = new SyncedStatusResponse { FccTransactionIds = [.. fccIds] };
        return FakeHandler.RespondJson(JsonSerializer.Serialize(response));
    }

    private static HttpMessageHandler RespondWithEmptySync()
        => RespondWithSyncedIds();

    // ── No token ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_NoToken_ReturnsZeroWithoutHttpCall()
    {
        _tokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        int callCount = 0;
        var handler = new FakeHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var worker = CreateWorker(handler);
        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().Be(0);
        callCount.Should().Be(0, "no HTTP call should be made without a token");
    }

    // ── Empty cloud response ──────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_EmptyCloudResponse_ReturnsZeroAndAdvancesTimestamp()
    {
        var worker = CreateWorker(RespondWithEmptySync());
        var before = DateTimeOffset.UtcNow;

        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().Be(0);

        var state = await _db.SyncStates.FindAsync(1);
        state.Should().NotBeNull();
        state!.LastStatusSyncAt.Should().BeOnOrAfter(before);
    }

    // ── Happy path: Uploaded → SyncedToOdoo ──────────────────────────────────

    [Fact]
    public async Task PollAsync_CloudReturnsSyncedIds_AdvancesUploadedRecords()
    {
        await SeedUploadedTransactionAsync("FCC-SYNCED");
        var worker = CreateWorker(RespondWithSyncedIds("FCC-SYNCED"));

        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().Be(1);
        var stored = await _db.Transactions.AsNoTracking().SingleAsync();
        stored.SyncStatus.Should().Be(SyncStatus.SyncedToOdoo);
        stored.Status.Should().Be(TransactionStatus.SyncedToOdoo);
    }

    [Fact]
    public async Task PollAsync_MultipleIds_AdvancesAllMatchingRecords()
    {
        await SeedUploadedTransactionAsync("FCC-001");
        await SeedUploadedTransactionAsync("FCC-002");
        await SeedUploadedTransactionAsync("FCC-003");

        var worker = CreateWorker(RespondWithSyncedIds("FCC-001", "FCC-002", "FCC-003"));

        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().Be(3);
        var all = await _db.Transactions.AsNoTracking().ToListAsync();
        all.Should().AllSatisfy(t => t.SyncStatus.Should().Be(SyncStatus.SyncedToOdoo));
    }

    // ── Only Uploaded records are advanced ────────────────────────────────────

    [Fact]
    public async Task PollAsync_PendingRecord_NotAdvancedToSyncedToOdoo()
    {
        // Add a Pending (not yet uploaded) record — must not be touched
        var tx = new BufferedTransaction
        {
            Id = Guid.NewGuid().ToString(),
            FccTransactionId = "FCC-PENDING",
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            VolumeMicrolitres = 10_000_000,
            AmountMinorUnits = 15_000,
            UnitPriceMinorPerLitre = 1500,
            CurrencyCode = "ETB",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            CompletedAt = DateTimeOffset.UtcNow,
            FccVendor = "DOMS",
            IngestionSource = "EDGE_POLL",
            Status = TransactionStatus.Pending,
            SyncStatus = SyncStatus.Pending,
        };
        _db.Transactions.Add(tx);
        await _db.SaveChangesAsync();

        var worker = CreateWorker(RespondWithSyncedIds("FCC-PENDING"));
        await worker.PollAsync(CancellationToken.None);

        var stored = await _db.Transactions.AsNoTracking().SingleAsync();
        stored.SyncStatus.Should().Be(SyncStatus.Pending, "only Uploaded records transition to SyncedToOdoo");
    }

    // ── LastStatusSyncAt advances ─────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_UpdatesLastStatusSyncAt_AfterSuccessfulPoll()
    {
        var worker = CreateWorker(RespondWithEmptySync());
        var before = DateTimeOffset.UtcNow;

        await worker.PollAsync(CancellationToken.None);

        var state = await _db.SyncStates.FindAsync(1);
        state!.LastStatusSyncAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task PollAsync_SecondCall_UsesLastStatusSyncAtAsSinceParameter()
    {
        // Seed known timestamp in SyncState
        var knownTimestamp = DateTimeOffset.UtcNow.AddHours(-2);
        _db.SyncStates.Add(new SyncStateRecord
        {
            Id = 1,
            LastStatusSyncAt = knownTimestamp,
            UpdatedAt = knownTimestamp,
        });
        await _db.SaveChangesAsync();

        string? capturedUrl = null;
        var handler = new FakeHandler(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return FakeHandler.JsonResponse(
                JsonSerializer.Serialize(new SyncedStatusResponse()));
        });

        var worker = CreateWorker(handler);
        await worker.PollAsync(CancellationToken.None);

        capturedUrl.Should().NotBeNull();
        capturedUrl.Should().Contain("since=");
        // The since parameter should encode the known timestamp
        capturedUrl.Should().Contain(Uri.EscapeDataString(knownTimestamp.UtcDateTime.ToString("O")[..10]),
            "since should reference the stored LastStatusSyncAt date");
    }

    // ── 401 — token refresh ───────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_Http401_RefreshesTokenAndRetries()
    {
        await SeedUploadedTransactionAsync("FCC-401");

        int callCount = 0;
        var handler = new FakeHandler(req =>
        {
            callCount++;
            if (callCount == 1)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            return FakeHandler.JsonResponse(
                JsonSerializer.Serialize(new SyncedStatusResponse { FccTransactionIds = ["FCC-401"] }));
        });

        _tokenProvider.RefreshTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("refreshed-jwt-token"));

        var worker = CreateWorker(handler);
        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().Be(1);
        callCount.Should().Be(2);
        await _tokenProvider.Received(1).RefreshTokenAsync(Arg.Any<CancellationToken>());

        var stored = await _db.Transactions.AsNoTracking().SingleAsync();
        stored.SyncStatus.Should().Be(SyncStatus.SyncedToOdoo);
    }

    [Fact]
    public async Task PollAsync_Http401_TokenRefreshFails_ReturnsZero()
    {
        var handler = FakeHandler.RespondStatus(HttpStatusCode.Unauthorized);
        _tokenProvider.RefreshTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var worker = CreateWorker(handler);
        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().Be(0);
    }

    // ── 403 decommission ──────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_Http403Decommissioned_HaltsAllFuturePolls()
    {
        var handler = FakeHandler.RespondJson(
            """{"error":"DEVICE_DECOMMISSIONED","message":"Device removed from site"}""",
            HttpStatusCode.Forbidden);

        var worker = CreateWorker(handler);

        var result1 = await worker.PollAsync(CancellationToken.None);
        result1.Should().Be(0);

        // Subsequent call must not make an HTTP call
        int callCount = 0;
        var countingHandler = new FakeHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        // Decommission state is on the worker instance, handler change doesn't matter
        var result2 = await worker.PollAsync(CancellationToken.None);
        result2.Should().Be(0);
        callCount.Should().Be(0, "decommissioned worker must make no further HTTP calls");
    }

    // ── Network error ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_NetworkError_ReturnsZeroWithoutThrowingOrAdvancingState()
    {
        await SeedUploadedTransactionAsync("FCC-NET");

        var handler = FakeHandler.ThrowNetworkError();
        var worker = CreateWorker(handler);

        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().Be(0);
        var stored = await _db.Transactions.AsNoTracking().SingleAsync();
        stored.SyncStatus.Should().Be(SyncStatus.Uploaded, "record must remain Uploaded on poll failure");
    }

    // ── SyncedToOdoo records excluded from local API ──────────────────────────

    [Fact]
    public async Task PollAsync_AfterAdvancing_RecordsExcludedFromLocalApiQuery()
    {
        await SeedUploadedTransactionAsync("FCC-EXCL");
        var worker = CreateWorker(RespondWithSyncedIds("FCC-EXCL"));
        await worker.PollAsync(CancellationToken.None);

        // Verify GetForLocalApiAsync excludes SyncedToOdoo records
        using var scope = _serviceProvider.CreateScope();
        var bufferManager = scope.ServiceProvider.GetRequiredService<TransactionBufferManager>();

        var apiResults = await bufferManager.GetForLocalApiAsync(null, 100, 0, CancellationToken.None);

        apiResults.Should().BeEmpty("SyncedToOdoo records must not appear in local API responses");
    }
}

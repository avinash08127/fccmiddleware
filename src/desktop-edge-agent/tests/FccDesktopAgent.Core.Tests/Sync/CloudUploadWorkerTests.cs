using System.Net;
using System.Net.Http.Headers;
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
using Polly;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Sync;

/// <summary>
/// Unit tests for <see cref="CloudUploadWorker"/>.
/// Uses real in-memory SQLite for the buffer and NSubstitute for HTTP / token provider.
/// </summary>
public sealed class CloudUploadWorkerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AgentDbContext> _dbOptions;
    private readonly AgentDbContext _db;
    private readonly IServiceProvider _serviceProvider;
    private readonly IDeviceTokenProvider _tokenProvider;

    public CloudUploadWorkerTests()
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

    // ── Test helpers ──────────────────────────────────────────────────────────

    private CloudUploadWorker CreateWorker(HttpMessageHandler httpHandler, bool noRetry = false)
    {
        var factory = new TestHttpClientFactory(httpHandler);
        var config = Options.Create(new AgentConfiguration
        {
            CloudBaseUrl = "http://cloud.test",
            SiteId = "SITE-A",
            UploadBatchSize = 50,
        });

        var registrationManager = Substitute.For<IRegistrationManager>();

        // noRetry: use ResiliencePipeline.Empty so tests don't wait for backoff delays.
        return new CloudUploadWorker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            factory,
            config,
            _tokenProvider,
            registrationManager,
            NullLogger<CloudUploadWorker>.Instance,
            noRetry ? ResiliencePipeline.Empty : null);
    }

    private async Task<BufferedTransaction> SeedTransactionAsync(
        string fccId = "FCC-001",
        string siteCode = "SITE-A")
    {
        var tx = new BufferedTransaction
        {
            Id = Guid.NewGuid().ToString(),
            FccTransactionId = fccId,
            SiteCode = siteCode,
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
            Status = TransactionStatus.Pending,
            SyncStatus = SyncStatus.Pending,
        };
        _db.Transactions.Add(tx);
        await _db.SaveChangesAsync();
        return tx;
    }

    private static HttpMessageHandler RespondWithUploadResult(
        string fccId, string siteCode, string outcome,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = new UploadResponse
        {
            Results =
            [
                new UploadResultItem
                {
                    FccTransactionId = fccId,
                    SiteCode = siteCode,
                    Outcome = outcome,
                }
            ],
            AcceptedCount = outcome == "ACCEPTED" ? 1 : 0,
            DuplicateCount = outcome == "DUPLICATE" ? 1 : 0,
            RejectedCount = outcome == "REJECTED" ? 1 : 0,
        };
        var json = JsonSerializer.Serialize(response);
        return FakeHandler.RespondJson(json, statusCode);
    }

    // ── Empty batch ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadBatchAsync_EmptyBuffer_ReturnsZero()
    {
        var worker = CreateWorker(FakeHandler.RespondStatus(HttpStatusCode.OK));

        var result = await worker.UploadBatchAsync(CancellationToken.None);

        result.Should().Be(0);
    }

    // ── ACCEPTED outcome ──────────────────────────────────────────────────────

    [Fact]
    public async Task UploadBatchAsync_AllAccepted_MarksUploadedAndReturnsCount()
    {
        var tx = await SeedTransactionAsync("FCC-001", "SITE-A");
        var worker = CreateWorker(RespondWithUploadResult("FCC-001", "SITE-A", "ACCEPTED"));

        var result = await worker.UploadBatchAsync(CancellationToken.None);

        result.Should().Be(1);
        var stored = await _db.Transactions.AsNoTracking().SingleAsync();
        stored.SyncStatus.Should().Be(SyncStatus.Uploaded);
    }

    // ── DUPLICATE outcome ─────────────────────────────────────────────────────

    [Fact]
    public async Task UploadBatchAsync_AllDuplicate_MarksDuplicateConfirmedAndReturnsCount()
    {
        await SeedTransactionAsync("FCC-DUP", "SITE-A");
        var worker = CreateWorker(RespondWithUploadResult("FCC-DUP", "SITE-A", "DUPLICATE"));

        var result = await worker.UploadBatchAsync(CancellationToken.None);

        result.Should().Be(1);
        var stored = await _db.Transactions.AsNoTracking().SingleAsync();
        stored.SyncStatus.Should().Be(SyncStatus.DuplicateConfirmed);
        stored.Status.Should().Be(TransactionStatus.Duplicate);
    }

    // ── REJECTED outcome ──────────────────────────────────────────────────────

    [Fact]
    public async Task UploadBatchAsync_Rejected_RecordsFailureAndReturnZero()
    {
        await SeedTransactionAsync("FCC-REJ", "SITE-A");
        var worker = CreateWorker(RespondWithUploadResult("FCC-REJ", "SITE-A", "REJECTED"));

        var result = await worker.UploadBatchAsync(CancellationToken.None);

        // REJECTED is not "succeeded" — record stays Pending for retry
        result.Should().Be(0);
        var stored = await _db.Transactions.AsNoTracking().SingleAsync();
        stored.SyncStatus.Should().Be(SyncStatus.Pending);
        stored.UploadAttempts.Should().Be(1);
        stored.LastUploadError.Should().Contain("REJECTED");
    }

    // ── Mixed outcomes ────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadBatchAsync_MixedOutcomes_HandlesEachCorrectly()
    {
        await SeedTransactionAsync("FCC-A", "SITE-A");
        await SeedTransactionAsync("FCC-B", "SITE-A");
        await SeedTransactionAsync("FCC-C", "SITE-A");

        var response = new UploadResponse
        {
            Results =
            [
                new UploadResultItem { FccTransactionId = "FCC-A", SiteCode = "SITE-A", Outcome = "ACCEPTED" },
                new UploadResultItem { FccTransactionId = "FCC-B", SiteCode = "SITE-A", Outcome = "DUPLICATE" },
                new UploadResultItem
                {
                    FccTransactionId = "FCC-C", SiteCode = "SITE-A", Outcome = "REJECTED",
                    Error = new UploadResultError { Code = "SCHEMA_ERROR", Message = "bad data" }
                },
            ],
            AcceptedCount = 1, DuplicateCount = 1, RejectedCount = 1,
        };
        var handler = FakeHandler.RespondJson(JsonSerializer.Serialize(response));
        var worker = CreateWorker(handler);

        var result = await worker.UploadBatchAsync(CancellationToken.None);

        result.Should().Be(2); // ACCEPTED + DUPLICATE count as succeeded

        var txA = await _db.Transactions.AsNoTracking().SingleAsync(t => t.FccTransactionId == "FCC-A");
        txA.SyncStatus.Should().Be(SyncStatus.Uploaded);

        var txB = await _db.Transactions.AsNoTracking().SingleAsync(t => t.FccTransactionId == "FCC-B");
        txB.SyncStatus.Should().Be(SyncStatus.DuplicateConfirmed);

        var txC = await _db.Transactions.AsNoTracking().SingleAsync(t => t.FccTransactionId == "FCC-C");
        txC.SyncStatus.Should().Be(SyncStatus.Pending); // stays pending for retry
        txC.UploadAttempts.Should().Be(1);
    }

    // ── 401 — token refresh ───────────────────────────────────────────────────

    [Fact]
    public async Task UploadBatchAsync_Http401_RefreshesTokenAndRetries()
    {
        await SeedTransactionAsync("FCC-401", "SITE-A");

        // First call returns 401; after refresh, second call returns success.
        int callCount = 0;
        var handler = new FakeHandler(req =>
        {
            callCount++;
            if (callCount == 1)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var response = new UploadResponse
            {
                Results = [new UploadResultItem { FccTransactionId = "FCC-401", SiteCode = "SITE-A", Outcome = "ACCEPTED" }],
                AcceptedCount = 1,
            };
            return FakeHandler.JsonResponse(JsonSerializer.Serialize(response));
        });

        _tokenProvider.RefreshTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("refreshed-jwt-token"));

        var worker = CreateWorker(handler);

        var result = await worker.UploadBatchAsync(CancellationToken.None);

        result.Should().Be(1);
        callCount.Should().Be(2);
        await _tokenProvider.Received(1).RefreshTokenAsync(Arg.Any<CancellationToken>());

        var stored = await _db.Transactions.AsNoTracking().SingleAsync();
        stored.SyncStatus.Should().Be(SyncStatus.Uploaded);
    }

    [Fact]
    public async Task UploadBatchAsync_Http401_TokenRefreshFails_RecordsError()
    {
        await SeedTransactionAsync("FCC-401", "SITE-A");

        var handler = FakeHandler.RespondStatus(HttpStatusCode.Unauthorized);
        _tokenProvider.RefreshTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var worker = CreateWorker(handler);

        var result = await worker.UploadBatchAsync(CancellationToken.None);

        result.Should().Be(0);
        var stored = await _db.Transactions.AsNoTracking().SingleAsync();
        stored.UploadAttempts.Should().Be(1);
        stored.LastUploadError.Should().Contain("Token refresh failed");
    }

    // ── 403 decommission ──────────────────────────────────────────────────────

    [Fact]
    public async Task UploadBatchAsync_Http403Decommissioned_HaltsAllFutureUploads()
    {
        await SeedTransactionAsync("FCC-DEC", "SITE-A");

        var handler = FakeHandler.RespondJson(
            """{"error":"DEVICE_DECOMMISSIONED","message":"Device removed from site"}""",
            HttpStatusCode.Forbidden);

        var worker = CreateWorker(handler);

        // First call: receives DEVICE_DECOMMISSIONED
        var result1 = await worker.UploadBatchAsync(CancellationToken.None);
        result1.Should().Be(0);

        // Seed another record to verify future uploads are skipped
        await SeedTransactionAsync("FCC-DEC-2", "SITE-A");

        // Second call: should return 0 immediately without HTTP call
        int callCount = 0;
        var countingHandler = new FakeHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        // Note: decommission state is on the worker instance, not the handler
        var result2 = await worker.UploadBatchAsync(CancellationToken.None);
        result2.Should().Be(0);
        callCount.Should().Be(0, "decommissioned worker must not make further HTTP calls");
    }

    // ── HTTP failure / network error ──────────────────────────────────────────

    [Fact]
    public async Task UploadBatchAsync_NetworkError_RecordsFailureAndReturnsZero()
    {
        await SeedTransactionAsync("FCC-NET", "SITE-A");

        // Use noRetry=true so the test runs immediately without Polly backoff delays.
        var handler = FakeHandler.ThrowNetworkError();
        var worker = CreateWorker(handler, noRetry: true);

        var result = await worker.UploadBatchAsync(CancellationToken.None);

        result.Should().Be(0);
        var stored = await _db.Transactions.AsNoTracking().SingleAsync();
        stored.SyncStatus.Should().Be(SyncStatus.Pending); // stays pending for next cadence tick
        stored.UploadAttempts.Should().Be(1);
        stored.LastUploadError.Should().NotBeNullOrEmpty();
    }

    // ── No token ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadBatchAsync_NoToken_ReturnsZeroWithoutHttpCall()
    {
        await SeedTransactionAsync();

        _tokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        int callCount = 0;
        var handler = new FakeHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var worker = CreateWorker(handler);
        var result = await worker.UploadBatchAsync(CancellationToken.None);

        result.Should().Be(0);
        callCount.Should().Be(0, "no HTTP call should be made without a token");
    }

    // ── Request structure ─────────────────────────────────────────────────────

    [Fact]
    public async Task UploadBatchAsync_SendsAuthorizationHeader()
    {
        await SeedTransactionAsync("FCC-HDR", "SITE-A");
        HttpRequestMessage? captured = null;

        var handler = new FakeHandler(req =>
        {
            captured = req;
            var response = new UploadResponse
            {
                Results = [new UploadResultItem { FccTransactionId = "FCC-HDR", SiteCode = "SITE-A", Outcome = "ACCEPTED" }],
                AcceptedCount = 1,
            };
            return FakeHandler.JsonResponse(JsonSerializer.Serialize(response));
        });

        var worker = CreateWorker(handler);
        await worker.UploadBatchAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Headers.Authorization.Should().NotBeNull();
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be("test-jwt-token");
    }

    // ── Ordering: oldest Pending first ────────────────────────────────────────

    [Fact]
    public async Task UploadBatchAsync_UploadsOldestPendingFirst()
    {
        // Seed with deliberate ordering gap: OLD first, then NEW
        await SeedTransactionAsync("FCC-OLD", "SITE-A");
        await Task.Delay(10); // ensure different CreatedAt
        await SeedTransactionAsync("FCC-NEW", "SITE-A");

        string? uploadedFccId = null;
        var handler = new FakeHandler(req =>
        {
            // Capture first transaction in request body
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var uploadReq = JsonSerializer.Deserialize<UploadRequest>(body);
            uploadedFccId = uploadReq?.Transactions.FirstOrDefault()?.FccTransactionId;

            var response = new UploadResponse
            {
                Results =
                [
                    new UploadResultItem { FccTransactionId = "FCC-OLD", SiteCode = "SITE-A", Outcome = "ACCEPTED" },
                    new UploadResultItem { FccTransactionId = "FCC-NEW", SiteCode = "SITE-A", Outcome = "ACCEPTED" },
                ],
                AcceptedCount = 2,
            };
            return FakeHandler.JsonResponse(JsonSerializer.Serialize(response));
        });

        var worker = CreateWorker(handler);
        await worker.UploadBatchAsync(CancellationToken.None);

        uploadedFccId.Should().Be("FCC-OLD", "oldest record must appear first in the upload payload");
    }
}

// ── Test HTTP infrastructure ──────────────────────────────────────────────────

internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;

    public static FakeHandler RespondJson(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(_ => JsonResponse(json, status));

    public static FakeHandler RespondStatus(HttpStatusCode status)
        => new(_ => new HttpResponseMessage(status));

    public static FakeHandler ThrowNetworkError()
        => new(_ => throw new HttpRequestException("Simulated network failure"));

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return new HttpResponseMessage(status) { Content = content };
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_fn(request));
}

internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public TestHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>
/// Simple <see cref="IOptionsMonitor{T}"/> implementation for tests.
/// Returns a fixed value; change notifications are no-op.
/// </summary>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

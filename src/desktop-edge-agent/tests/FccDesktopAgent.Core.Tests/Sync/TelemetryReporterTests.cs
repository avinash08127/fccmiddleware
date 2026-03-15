using System.Net;
using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Sync;
// ReSharper disable AccessToDisposedClosure
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
/// Unit tests for <see cref="TelemetryReporter"/>.
/// Uses in-memory SQLite for buffer stats and NSubstitute for HTTP, connectivity, and token provider.
/// </summary>
public sealed class TelemetryReporterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AgentDbContext> _dbOptions;
    private readonly AgentDbContext _db;
    private readonly IServiceProvider _serviceProvider;
    private readonly IDeviceTokenProvider _tokenProvider;
    private readonly IConnectivityMonitor _connectivity;
    private readonly IErrorCountTracker _errorTracker;
    private readonly FakeHttpMessageHandler _httpHandler;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfigManager _configManager;
    private readonly AgentConfiguration _config;

    public TelemetryReporterTests()
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

        _connectivity = Substitute.For<IConnectivityMonitor>();
        _connectivity.Current.Returns(new ConnectivitySnapshot(
            FccDesktopAgent.Core.Connectivity.ConnectivityState.FullyOnline, true, true, DateTimeOffset.UtcNow));
        _connectivity.LastFccSuccessAtUtc.Returns(DateTimeOffset.UtcNow.AddMinutes(-1));
        _connectivity.FccConsecutiveFailures.Returns(0);

        _errorTracker = new ErrorCountTracker();

        _httpHandler = new FakeHttpMessageHandler();
        _httpFactory = Substitute.For<IHttpClientFactory>();
        _httpFactory.CreateClient("cloud")
            .Returns(_ => new HttpClient(_httpHandler));

        _configManager = Substitute.For<IConfigManager>();

        _config = new AgentConfiguration
        {
            DeviceId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            SiteId = "site-001",
            LegalEntityId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            CloudBaseUrl = "https://api.example.com",
            FccBaseUrl = "http://192.168.1.100:8080",
            FccVendor = FccDesktopAgent.Core.Adapter.Common.FccVendor.Doms,
            UploadBatchSize = 50,
            TelemetryIntervalSeconds = 300,
        };

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
        _httpHandler.Dispose();
    }

    private TelemetryReporter CreateReporter()
    {
        var scopeFactory = (_serviceProvider as ServiceProvider)!
            .GetRequiredService<IServiceScopeFactory>();

        var registrationManager = Substitute.For<IRegistrationManager>();
        var authHandler = new AuthenticatedCloudRequestHandler(
            _tokenProvider, registrationManager,
            NullLogger<AuthenticatedCloudRequestHandler>.Instance);

        return new TelemetryReporter(
            scopeFactory,
            _httpFactory,
            Options.Create(_config),
            _connectivity,
            authHandler,
            registrationManager,
            _errorTracker,
            _configManager,
            NullLogger<TelemetryReporter>.Instance);
    }

    // ── Success path ──────────────────────────────────────────────────────

    [Fact]
    public async Task ReportAsync_OnSuccess_ReturnsTrue()
    {
        _httpHandler.SetResponse(HttpStatusCode.NoContent);
        var reporter = CreateReporter();

        var result = await reporter.ReportAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ReportAsync_OnSuccess_SendsPostToTelemetryEndpoint()
    {
        _httpHandler.SetResponse(HttpStatusCode.NoContent);
        var reporter = CreateReporter();

        await reporter.ReportAsync(CancellationToken.None);

        _httpHandler.LastRequest.Should().NotBeNull();
        _httpHandler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        _httpHandler.LastRequest.RequestUri!.ToString()
            .Should().Be("https://api.example.com/api/v1/agent/telemetry");
    }

    [Fact]
    public async Task ReportAsync_OnSuccess_IncludesBearerToken()
    {
        _httpHandler.SetResponse(HttpStatusCode.NoContent);
        var reporter = CreateReporter();

        await reporter.ReportAsync(CancellationToken.None);

        _httpHandler.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        _httpHandler.LastRequest.Headers.Authorization.Parameter.Should().Be("test-jwt-token");
    }

    [Fact]
    public async Task ReportAsync_SendsValidPayload()
    {
        _httpHandler.SetResponse(HttpStatusCode.NoContent);
        var reporter = CreateReporter();

        await reporter.ReportAsync(CancellationToken.None);

        var body = _httpHandler.LastRequestBody!;
        var payload = JsonSerializer.Deserialize<TelemetryPayload>(body);

        payload.Should().NotBeNull();
        payload!.SchemaVersion.Should().Be("1.0");
        payload.DeviceId.Should().Be(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        payload.SiteCode.Should().Be("site-001");
        payload.SequenceNumber.Should().Be(1);
        payload.ConnectivityState.Should().Be(CloudConnectivityState.FULLY_ONLINE);
    }

    [Fact]
    public async Task ReportAsync_SequenceNumber_Increments()
    {
        _httpHandler.SetResponse(HttpStatusCode.NoContent);
        var reporter = CreateReporter();

        await reporter.ReportAsync(CancellationToken.None);
        var body1 = _httpHandler.LastRequestBody!;
        var p1 = JsonSerializer.Deserialize<TelemetryPayload>(body1);

        await reporter.ReportAsync(CancellationToken.None);
        var body2 = _httpHandler.LastRequestBody!;
        var p2 = JsonSerializer.Deserialize<TelemetryPayload>(body2);

        p1!.SequenceNumber.Should().Be(1);
        p2!.SequenceNumber.Should().Be(2);
    }

    // ── Failure paths ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReportAsync_NoToken_ReturnsFalse()
    {
        _tokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var reporter = CreateReporter();
        var result = await reporter.ReportAsync(CancellationToken.None);

        result.Should().BeFalse();
        _httpHandler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ReportAsync_HttpError_ReturnsFalse()
    {
        _httpHandler.SetResponse(HttpStatusCode.InternalServerError);
        var reporter = CreateReporter();

        var result = await reporter.ReportAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ReportAsync_HttpException_ReturnsFalse()
    {
        _httpHandler.SetException(new HttpRequestException("Network error"));
        var reporter = CreateReporter();

        var result = await reporter.ReportAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── 401 + token refresh ───────────────────────────────────────────────

    [Fact]
    public async Task ReportAsync_On401_RefreshesTokenAndRetries()
    {
        // First call returns 401, second returns 204.
        var callCount = 0;
        _httpHandler.SetResponseFactory(_ =>
        {
            callCount++;
            return callCount == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        _tokenProvider.RefreshTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("new-jwt-token"));

        var reporter = CreateReporter();
        var result = await reporter.ReportAsync(CancellationToken.None);

        result.Should().BeTrue();
        await _tokenProvider.Received(1).RefreshTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportAsync_On401_FailedRefresh_ReturnsFalse()
    {
        _httpHandler.SetResponse(HttpStatusCode.Unauthorized);
        _tokenProvider.RefreshTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var reporter = CreateReporter();
        var result = await reporter.ReportAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── Error count reset behavior ────────────────────────────────────────

    [Fact]
    public async Task ReportAsync_OnSuccess_ResetsErrorCounts()
    {
        _httpHandler.SetResponse(HttpStatusCode.NoContent);
        _errorTracker.IncrementFccConnectionErrors();
        _errorTracker.IncrementCloudUploadErrors();

        var reporter = CreateReporter();
        await reporter.ReportAsync(CancellationToken.None);

        var snapshot = _errorTracker.Peek();
        snapshot.FccConnectionErrors.Should().Be(0);
        snapshot.CloudUploadErrors.Should().Be(0);
    }

    [Fact]
    public async Task ReportAsync_OnFailure_DoesNotResetErrorCounts()
    {
        _httpHandler.SetResponse(HttpStatusCode.InternalServerError);
        _errorTracker.IncrementFccConnectionErrors();

        var reporter = CreateReporter();
        await reporter.ReportAsync(CancellationToken.None);

        var snapshot = _errorTracker.Peek();
        snapshot.FccConnectionErrors.Should().Be(1);
    }

    // ── Payload content validation ────────────────────────────────────────

    [Fact]
    public async Task ReportAsync_PopulatesDeviceStatus()
    {
        _httpHandler.SetResponse(HttpStatusCode.NoContent);
        var reporter = CreateReporter();

        await reporter.ReportAsync(CancellationToken.None);

        var body = _httpHandler.LastRequestBody!;
        var payload = JsonSerializer.Deserialize<TelemetryPayload>(body);

        payload!.Device.Should().NotBeNull();
        payload.Device.BatteryPercent.Should().Be(100); // Desktop always powered
        payload.Device.IsCharging.Should().BeTrue();
        payload.Device.AppVersion.Should().NotBeNullOrEmpty();
        payload.Device.OsVersion.Should().NotBeNullOrEmpty();
        payload.Device.DeviceModel.Should().NotBeNullOrEmpty();
        payload.Device.AppUptimeSeconds.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task ReportAsync_PopulatesFccHealth()
    {
        _httpHandler.SetResponse(HttpStatusCode.NoContent);
        var reporter = CreateReporter();

        await reporter.ReportAsync(CancellationToken.None);

        var body = _httpHandler.LastRequestBody!;
        var payload = JsonSerializer.Deserialize<TelemetryPayload>(body);

        payload!.FccHealth.Should().NotBeNull();
        payload.FccHealth.IsReachable.Should().BeTrue();
        payload.FccHealth.FccVendor.Should().Be(CloudFccVendor.DOMS);
        payload.FccHealth.FccHost.Should().Be("192.168.1.100");
        payload.FccHealth.FccPort.Should().Be(8080);
        payload.FccHealth.ConsecutiveHeartbeatFailures.Should().Be(0);
    }

    [Fact]
    public async Task ReportAsync_PopulatesBufferStats()
    {
        // Seed some buffered transactions.
        using (var scope = (_serviceProvider as ServiceProvider)!.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
            db.Transactions.Add(CreateBufferedTransaction(SyncStatus.Pending));
            db.Transactions.Add(CreateBufferedTransaction(SyncStatus.Pending));
            db.Transactions.Add(CreateBufferedTransaction(SyncStatus.Uploaded));
            await db.SaveChangesAsync();
        }

        _httpHandler.SetResponse(HttpStatusCode.NoContent);
        var reporter = CreateReporter();

        await reporter.ReportAsync(CancellationToken.None);

        var body = _httpHandler.LastRequestBody!;
        var payload = JsonSerializer.Deserialize<TelemetryPayload>(body);

        payload!.Buffer.Should().NotBeNull();
        payload.Buffer.TotalRecords.Should().Be(3);
        payload.Buffer.PendingUploadCount.Should().Be(2);
        payload.Buffer.SyncedCount.Should().Be(1);
    }

    [Fact]
    public async Task ReportAsync_PopulatesSyncStatus()
    {
        // Seed sync state record
        using (var scope = (_serviceProvider as ServiceProvider)!.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
            db.SyncStates.Add(new SyncStateRecord
            {
                Id = 1,
                LastUploadAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastStatusSyncAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                ConfigVersion = "v2.1",
            });
            await db.SaveChangesAsync();
        }

        _httpHandler.SetResponse(HttpStatusCode.NoContent);
        var reporter = CreateReporter();

        await reporter.ReportAsync(CancellationToken.None);

        var body = _httpHandler.LastRequestBody!;
        var payload = JsonSerializer.Deserialize<TelemetryPayload>(body);

        payload!.Sync.Should().NotBeNull();
        payload.Sync.ConfigVersion.Should().Be("v2.1");
        payload.Sync.UploadBatchSize.Should().Be(50);
    }

    [Fact]
    public async Task ReportAsync_PopulatesErrorCounts()
    {
        _errorTracker.IncrementFccConnectionErrors();
        _errorTracker.IncrementFccConnectionErrors();
        _errorTracker.IncrementCloudUploadErrors();

        _httpHandler.SetResponse(HttpStatusCode.NoContent);
        var reporter = CreateReporter();

        await reporter.ReportAsync(CancellationToken.None);

        var body = _httpHandler.LastRequestBody!;
        var payload = JsonSerializer.Deserialize<TelemetryPayload>(body);

        payload!.ErrorCounts.Should().NotBeNull();
        payload.ErrorCounts.FccConnectionErrors.Should().Be(2);
        payload.ErrorCounts.CloudUploadErrors.Should().Be(1);
    }

    [Fact]
    public async Task ReportAsync_MapsConnectivityStates()
    {
        _httpHandler.SetResponse(HttpStatusCode.NoContent);

        // Test each connectivity state mapping
        var states = new[]
        {
            (FccDesktopAgent.Core.Connectivity.ConnectivityState.FullyOnline, CloudConnectivityState.FULLY_ONLINE),
            (FccDesktopAgent.Core.Connectivity.ConnectivityState.InternetDown, CloudConnectivityState.INTERNET_DOWN),
            (FccDesktopAgent.Core.Connectivity.ConnectivityState.FccUnreachable, CloudConnectivityState.FCC_UNREACHABLE),
            (FccDesktopAgent.Core.Connectivity.ConnectivityState.FullyOffline, CloudConnectivityState.FULLY_OFFLINE),
        };

        foreach (var (state, expected) in states)
        {
            _connectivity.Current.Returns(new ConnectivitySnapshot(
                state,
                state != FccDesktopAgent.Core.Connectivity.ConnectivityState.InternetDown
                    && state != FccDesktopAgent.Core.Connectivity.ConnectivityState.FullyOffline,
                state != FccDesktopAgent.Core.Connectivity.ConnectivityState.FccUnreachable
                    && state != FccDesktopAgent.Core.Connectivity.ConnectivityState.FullyOffline,
                DateTimeOffset.UtcNow));

            var reporter = CreateReporter();
            await reporter.ReportAsync(CancellationToken.None);

            var body = _httpHandler.LastRequestBody!;
            var payload = JsonSerializer.Deserialize<TelemetryPayload>(body);
            payload!.ConnectivityState.Should().Be(expected, $"state {state} should map to {expected}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static BufferedTransaction CreateBufferedTransaction(SyncStatus syncStatus) => new()
    {
        Id = Guid.NewGuid().ToString(),
        FccTransactionId = Guid.NewGuid().ToString(),
        SiteCode = "site-001",
        PumpNumber = 1,
        NozzleNumber = 1,
        ProductCode = "PMS",
        VolumeMicrolitres = 10_000_000,
        AmountMinorUnits = 5000,
        UnitPriceMinorPerLitre = 500,
        CurrencyCode = "USD",
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        FccVendor = "DOMS",
        Status = FccDesktopAgent.Core.Adapter.Common.TransactionStatus.Pending,
        SyncStatus = syncStatus,
        IngestionSource = "EdgeUpload",
        RawPayloadJson = "{}",
        CorrelationId = Guid.NewGuid().ToString(),
        SchemaVersion = "1.0",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Simple fake HTTP handler for testing — allows setting canned responses or exceptions.
    /// Eagerly captures the request body because the caller may dispose the request.
    /// </summary>
    internal sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode = HttpStatusCode.NoContent;
        private Exception? _exception;
        private Func<HttpRequestMessage, HttpResponseMessage>? _responseFactory;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public void SetResponse(HttpStatusCode statusCode) => _statusCode = statusCode;
        public void SetException(Exception exception) => _exception = exception;
        public void SetResponseFactory(Func<HttpRequestMessage, HttpResponseMessage> factory)
            => _responseFactory = factory;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Eagerly capture the body before the caller disposes the request.
            LastRequest = request;
            LastRequestBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;

            if (_exception is not null)
                throw _exception;

            if (_responseFactory is not null)
                return _responseFactory(request);

            return new HttpResponseMessage(_statusCode);
        }
    }
}

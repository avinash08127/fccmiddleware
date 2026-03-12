using System.Net;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Adapter.Doms;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Adapter.Doms;

public sealed class DomsAdapterTests
{
    private const string BaseUrl = "http://192.168.1.100:8080";
    private const string ApiKey = "test-api-key";
    private const string SiteCode = "SITE-001";

    private static readonly FccConnectionConfig DefaultConfig = new(
        BaseUrl: BaseUrl,
        ApiKey: ApiKey,
        RequestTimeout: TimeSpan.FromSeconds(10),
        SiteCode: SiteCode);

    private static DomsAdapter BuildAdapter(TestHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("fcc").Returns(httpClient);
        return new DomsAdapter(factory, DefaultConfig, NullLogger<DomsAdapter>.Instance);
    }

    // ── NormalizeAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task NormalizeAsync_MapsAllFieldsCorrectly()
    {
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondStatus(HttpStatusCode.OK));
        var rawJson = """
            {
              "transactionId": "TX-001",
              "pumpNumber": 3,
              "nozzleNumber": 1,
              "productCode": "DIESEL",
              "volumeLitres": 50.5,
              "amountMinorUnits": 12625,
              "unitPriceMinorPerLitre": 250,
              "currencyCode": "KES",
              "startedAt": "2024-01-01T10:00:00Z",
              "completedAt": "2024-01-01T10:05:00Z",
              "fiscalReceiptNumber": "RCP-001",
              "attendantId": "ATT-01"
            }
            """;
        var envelope = new RawPayloadEnvelope("DOMS", SiteCode, rawJson, DateTimeOffset.UtcNow);

        var result = await adapter.NormalizeAsync(envelope, CancellationToken.None);

        result.FccTransactionId.Should().Be("TX-001");
        result.SiteCode.Should().Be(SiteCode);
        result.PumpNumber.Should().Be(3);
        result.NozzleNumber.Should().Be(1);
        result.ProductCode.Should().Be("DIESEL");
        result.VolumeMicrolitres.Should().Be(50_500_000L); // 50.5 L × 1_000_000
        result.AmountMinorUnits.Should().Be(12625L);
        result.UnitPriceMinorPerLitre.Should().Be(250L);
        result.CurrencyCode.Should().Be("KES");
        result.StartedAt.Should().Be(DateTimeOffset.Parse("2024-01-01T10:00:00Z"));
        result.CompletedAt.Should().Be(DateTimeOffset.Parse("2024-01-01T10:05:00Z"));
        result.FiscalReceiptNumber.Should().Be("RCP-001");
        result.AttendantId.Should().Be("ATT-01");
        result.FccVendor.Should().Be("DOMS");
        result.RawPayloadJson.Should().Be(rawJson);
    }

    [Fact]
    public async Task NormalizeAsync_ConvertsVolumeLitresToMicrolitres_UsingDecimalArithmetic()
    {
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondStatus(HttpStatusCode.OK));
        var rawJson = """
            {
              "transactionId": "TX-002",
              "pumpNumber": 1, "nozzleNumber": 1,
              "productCode": "PETROL",
              "volumeLitres": 1.000001,
              "amountMinorUnits": 250, "unitPriceMinorPerLitre": 250,
              "currencyCode": "KES",
              "startedAt": "2024-01-01T10:00:00Z",
              "completedAt": "2024-01-01T10:01:00Z"
            }
            """;
        var envelope = new RawPayloadEnvelope("DOMS", SiteCode, rawJson, DateTimeOffset.UtcNow);

        var result = await adapter.NormalizeAsync(envelope, CancellationToken.None);

        result.VolumeMicrolitres.Should().Be(1_000_001L);
    }

    // ── FetchTransactionsAsync ───────────────────────────────────────────────

    [Fact]
    public async Task FetchTransactionsAsync_ParsesResponseAndWrapsEnvelopes()
    {
        var fixture = LoadFixture("doms-transactions-response.json");
        var handler = TestHttpMessageHandler.RespondJson(fixture);
        var adapter = BuildAdapter(handler);

        var batch = await adapter.FetchTransactionsAsync(
            new FetchCursor(null, DateTimeOffset.UtcNow.AddHours(-1), 50),
            CancellationToken.None);

        batch.Records.Should().HaveCount(2);
        batch.NextCursor.Should().Be("cursor-page-2");
        batch.HasMore.Should().BeTrue();

        var first = batch.Records[0];
        first.FccVendor.Should().Be("DOMS");
        first.SiteCode.Should().Be(SiteCode);
        first.RawJson.Should().Contain("TX-20240101-001");
    }

    [Fact]
    public async Task FetchTransactionsAsync_BuildsCorrectUrl_WithCursorAndSince()
    {
        var handler = TestHttpMessageHandler.RespondJson("""{"transactions":[],"nextCursor":null,"hasMore":false}""");
        var adapter = BuildAdapter(handler);
        var since = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);

        await adapter.FetchTransactionsAsync(
            new FetchCursor("my-cursor", since, 25),
            CancellationToken.None);

        var requestUri = handler.LastRequest!.RequestUri!.ToString();
        requestUri.Should().Contain("/api/v1/transactions");
        requestUri.Should().Contain("limit=25");
        requestUri.Should().Contain("since=");
        requestUri.Should().Contain("cursor=my-cursor");
    }

    [Fact]
    public async Task FetchTransactionsAsync_IncludesApiKeyHeader()
    {
        var handler = TestHttpMessageHandler.RespondJson("""{"transactions":[],"nextCursor":null,"hasMore":false}""");
        var adapter = BuildAdapter(handler);

        await adapter.FetchTransactionsAsync(new FetchCursor(null, null), CancellationToken.None);

        handler.LastRequest!.Headers.Should().ContainSingle(h => h.Key == "X-API-Key");
        handler.LastRequest.Headers.GetValues("X-API-Key").Should().Contain(ApiKey);
    }

    [Fact]
    public async Task FetchTransactionsAsync_Returns_EmptyBatch_On401()
    {
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondStatus(HttpStatusCode.Unauthorized));

        var batch = await adapter.FetchTransactionsAsync(new FetchCursor(null, null), CancellationToken.None);

        batch.Records.Should().BeEmpty();
        batch.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task FetchTransactionsAsync_Returns_EmptyBatch_On503()
    {
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondStatus(HttpStatusCode.ServiceUnavailable));

        var batch = await adapter.FetchTransactionsAsync(new FetchCursor(null, null), CancellationToken.None);

        batch.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchTransactionsAsync_Returns_EmptyBatch_OnNetworkError()
    {
        var adapter = BuildAdapter(TestHttpMessageHandler.ThrowNetworkError());

        var batch = await adapter.FetchTransactionsAsync(new FetchCursor(null, null), CancellationToken.None);

        batch.Records.Should().BeEmpty();
    }

    // ── SendPreAuthAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SendPreAuthAsync_ReturnsAccepted_WhenFccAccepts()
    {
        var fixture = LoadFixture("doms-preauth-response-accepted.json");
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondJson(fixture));
        var command = BuildPreAuthCommand();

        var result = await adapter.SendPreAuthAsync(command, CancellationToken.None);

        result.Accepted.Should().BeTrue();
        result.FccCorrelationId.Should().Be("CORR-20240101-001");
        result.FccAuthorizationCode.Should().Be("AUTH-001");
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task SendPreAuthAsync_ReturnsRejected_WhenFccRejects()
    {
        var fixture = LoadFixture("doms-preauth-response-rejected.json");
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondJson(fixture));

        var result = await adapter.SendPreAuthAsync(BuildPreAuthCommand(), CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.ErrorCode.Should().Be("PUMP_BUSY");
        result.ErrorMessage.Should().Contain("Pump is already authorized");
    }

    [Fact]
    public async Task SendPreAuthAsync_SendsCorrectPayload()
    {
        var fixture = LoadFixture("doms-preauth-response-accepted.json");
        var handler = TestHttpMessageHandler.RespondJson(fixture);
        var adapter = BuildAdapter(handler);
        var command = BuildPreAuthCommand();

        await adapter.SendPreAuthAsync(command, CancellationToken.None);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequestBody.Should().Contain("\"pumpNumber\":1");
        handler.LastRequestBody.Should().Contain("\"nozzleNumber\":2");
        handler.LastRequestBody.Should().Contain("\"amountMinorUnits\":10000");
        handler.LastRequestBody.Should().Contain("\"currencyCode\":\"KES\"");
    }

    [Fact]
    public async Task SendPreAuthAsync_ReturnsFailure_On503()
    {
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondStatus(HttpStatusCode.ServiceUnavailable));

        var result = await adapter.SendPreAuthAsync(BuildPreAuthCommand(), CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.ErrorCode.Should().Contain("503");
    }

    // ── GetPumpStatusAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPumpStatusAsync_MapsPumpStatusCorrectly()
    {
        var fixture = LoadFixture("doms-pump-status-response.json");
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondJson(fixture));

        var statuses = await adapter.GetPumpStatusAsync(CancellationToken.None);

        statuses.Should().HaveCount(2);

        var idle = statuses.First(s => s.PumpNumber == 1);
        idle.State.Should().Be(PumpState.Idle);
        idle.NozzleNumber.Should().Be(1);
        idle.ProductCode.Should().Be("DIESEL");
        idle.SiteCode.Should().Be(SiteCode);
        idle.Source.Should().Be(PumpStatusSource.FccLive);

        var dispensing = statuses.First(s => s.PumpNumber == 2);
        dispensing.State.Should().Be(PumpState.Dispensing);
        dispensing.CurrentVolumeLitres.Should().Be("12.500");
    }

    [Fact]
    public async Task GetPumpStatusAsync_MapsAllPumpStates()
    {
        var states = new[]
        {
            ("IDLE", PumpState.Idle),
            ("AUTHORIZED", PumpState.Authorized),
            ("CALLING", PumpState.Calling),
            ("DISPENSING", PumpState.Dispensing),
            ("PAUSED", PumpState.Paused),
            ("COMPLETED", PumpState.Completed),
            ("ERROR", PumpState.Error),
            ("OFFLINE", PumpState.Offline),
            ("UNKNOWN_STATE", PumpState.Unknown),
        };

        foreach (var (domState, expected) in states)
        {
            var json = $$"""
                [{"pumpNumber":1,"nozzleNumber":1,"state":"{{domState}}","currencyCode":"KES","statusSequence":1}]
                """;
            var adapter = BuildAdapter(TestHttpMessageHandler.RespondJson(json));

            var statuses = await adapter.GetPumpStatusAsync(CancellationToken.None);

            statuses[0].State.Should().Be(expected, because: $"DOMS state '{domState}' should map to {expected}");
        }
    }

    [Fact]
    public async Task GetPumpStatusAsync_ReturnsEmpty_OnNetworkError()
    {
        var adapter = BuildAdapter(TestHttpMessageHandler.ThrowNetworkError());

        var statuses = await adapter.GetPumpStatusAsync(CancellationToken.None);

        statuses.Should().BeEmpty();
    }

    // ── HeartbeatAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task HeartbeatAsync_ReturnsTrue_WhenFccRespondsUp()
    {
        var fixture = LoadFixture("doms-heartbeat-up.json");
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondJson(fixture));

        var alive = await adapter.HeartbeatAsync(CancellationToken.None);

        alive.Should().BeTrue();
    }

    [Fact]
    public async Task HeartbeatAsync_ReturnsFalse_WhenFccRespondsNonSuccess()
    {
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondStatus(HttpStatusCode.ServiceUnavailable));

        var alive = await adapter.HeartbeatAsync(CancellationToken.None);

        alive.Should().BeFalse();
    }

    [Fact]
    public async Task HeartbeatAsync_ReturnsFalse_OnNetworkError()
    {
        var adapter = BuildAdapter(TestHttpMessageHandler.ThrowNetworkError());

        var alive = await adapter.HeartbeatAsync(CancellationToken.None);

        alive.Should().BeFalse();
    }

    [Fact]
    public async Task HeartbeatAsync_ReturnsFalse_WhenStatusIsNotUp()
    {
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondJson("""{ "status": "DOWN" }"""));

        var alive = await adapter.HeartbeatAsync(CancellationToken.None);

        alive.Should().BeFalse();
    }

    [Fact]
    public async Task HeartbeatAsync_HitsHeartbeatEndpoint()
    {
        var handler = TestHttpMessageHandler.RespondJson("""{ "status": "UP" }""");
        var adapter = BuildAdapter(handler);

        await adapter.HeartbeatAsync(CancellationToken.None);

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/v1/heartbeat");
        handler.LastRequest.Method.Should().Be(HttpMethod.Get);
    }

    // ── Error classification ─────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchTransactions_LogsNonRecoverableOnAuthErrors(HttpStatusCode statusCode)
    {
        // Non-recoverable errors return empty batch (not exception) per IFccAdapter contract
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondStatus(statusCode));

        var batch = await adapter.FetchTransactionsAsync(new FetchCursor(null, null), CancellationToken.None);

        batch.Records.Should().BeEmpty("auth errors are non-recoverable and should return empty batch");
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task FetchTransactions_LogsRecoverableOnTransientErrors(HttpStatusCode statusCode)
    {
        var adapter = BuildAdapter(TestHttpMessageHandler.RespondStatus(statusCode));

        var batch = await adapter.FetchTransactionsAsync(new FetchCursor(null, null), CancellationToken.None);

        batch.Records.Should().BeEmpty("transient errors return empty batch and can be retried");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PreAuthCommand BuildPreAuthCommand() => new(
        PreAuthId: Guid.NewGuid().ToString(),
        SiteCode: SiteCode,
        FccPumpNumber: 1,
        FccNozzleNumber: 2,
        ProductCode: "DIESEL",
        RequestedAmountMinorUnits: 10_000L,
        UnitPriceMinorPerLitre: 250L,
        Currency: "KES",
        VehicleNumber: "KCA-123A",
        FccCorrelationId: null);

    private static string LoadFixture(string filename)
    {
        var dir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Adapter", "Doms", "Fixtures");
        return File.ReadAllText(Path.Combine(dir, filename));
    }
}

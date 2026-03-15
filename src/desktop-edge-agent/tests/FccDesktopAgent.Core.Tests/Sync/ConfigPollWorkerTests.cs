using System.Net;
using System.Text.Json;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Sync;

/// <summary>
/// Unit tests for <see cref="ConfigPollWorker"/>.
/// Uses NSubstitute for HTTP, token provider, and config manager.
/// </summary>
public sealed class ConfigPollWorkerTests
{
    private readonly IDeviceTokenProvider _tokenProvider;
    private readonly IConfigManager _configManager;

    public ConfigPollWorkerTests()
    {
        _tokenProvider = Substitute.For<IDeviceTokenProvider>();
        _tokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("test-jwt-token"));

        _configManager = Substitute.For<IConfigManager>();
        _configManager.CurrentConfigVersion.Returns((string?)null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ConfigPollWorker CreateWorker(HttpMessageHandler httpHandler)
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

        return new ConfigPollWorker(
            factory,
            config,
            _configManager,
            authHandler,
            registrationManager,
            NullLogger<ConfigPollWorker>.Instance);
    }

    private static string BuildSiteConfigJson(int configVersion = 1, DateTimeOffset? effectiveAt = null)
    {
        var config = TestSiteConfigFactory.Create(configVersion, effectiveAt);
        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    private static HttpMessageHandler RespondWithConfig(
        int configVersion = 1,
        string? etag = null,
        DateTimeOffset? effectiveAt = null)
    {
        return new FakeHandler(_ =>
        {
            var json = BuildSiteConfigJson(configVersion, effectiveAt);
            var response = FakeHandler.JsonResponse(json);
            if (etag is not null)
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{etag}\"");
            else
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{configVersion}\"");
            return response;
        });
    }

    // ── No token ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_NoToken_ReturnsFalseWithoutHttpCall()
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

        result.Should().BeFalse();
        callCount.Should().Be(0, "no HTTP call should be made without a token");
    }

    // ── 304 Not Modified ──────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_304NotModified_ReturnsFalseNoParsing()
    {
        _configManager.CurrentConfigVersion.Returns("42");

        var handler = FakeHandler.RespondStatus(HttpStatusCode.NotModified);
        var worker = CreateWorker(handler);

        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().BeFalse();
        await _configManager.DidNotReceive().ApplyConfigAsync(
            Arg.Any<SiteConfig>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Sends If-None-Match header ────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_SendsIfNoneMatchHeader_WithCurrentVersion()
    {
        _configManager.CurrentConfigVersion.Returns("42");

        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        var worker = CreateWorker(handler);
        await worker.PollAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Headers.TryGetValues("If-None-Match", out var values).Should().BeTrue();
        values.Should().Contain("\"42\"");
    }

    [Fact]
    public async Task PollAsync_NoCurrentVersion_OmitsIfNoneMatchHeader()
    {
        _configManager.CurrentConfigVersion.Returns((string?)null);

        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        var worker = CreateWorker(handler);
        await worker.PollAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Headers.TryGetValues("If-None-Match", out _).Should().BeFalse();
    }

    // ── 200 — new config applied ──────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_200WithNewConfig_AppliesAndReturnsTrue()
    {
        _configManager.ApplyConfigAsync(
                Arg.Any<SiteConfig>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConfigApplyResult(ConfigApplyOutcome.Applied, 1, ["initial"], []));

        var worker = CreateWorker(RespondWithConfig(configVersion: 1, etag: "1"));
        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().BeTrue();
        await _configManager.Received(1).ApplyConfigAsync(
            Arg.Any<SiteConfig>(), Arg.Any<string>(), Arg.Is("1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_200WithStaleVersion_ReturnsFalse()
    {
        _configManager.ApplyConfigAsync(
                Arg.Any<SiteConfig>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConfigApplyResult(ConfigApplyOutcome.StaleVersion, 1));

        var worker = CreateWorker(RespondWithConfig(configVersion: 1));
        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── Sends Authorization header ────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_SendsAuthorizationHeader()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        var worker = CreateWorker(handler);
        await worker.PollAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Headers.Authorization.Should().NotBeNull();
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be("test-jwt-token");
    }

    // ── 401 — token refresh ───────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_Http401_RefreshesTokenAndRetries()
    {
        _configManager.ApplyConfigAsync(
                Arg.Any<SiteConfig>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConfigApplyResult(ConfigApplyOutcome.Applied, 1, ["initial"], []));

        int callCount = 0;
        var handler = new FakeHandler(req =>
        {
            callCount++;
            if (callCount == 1)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var json = BuildSiteConfigJson();
            var resp = FakeHandler.JsonResponse(json);
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"1\"");
            return resp;
        });

        _tokenProvider.RefreshTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("refreshed-jwt-token"));

        var worker = CreateWorker(handler);
        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().BeTrue();
        callCount.Should().Be(2);
        await _tokenProvider.Received(1).RefreshTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_Http401_TokenRefreshFails_ReturnsFalse()
    {
        var handler = FakeHandler.RespondStatus(HttpStatusCode.Unauthorized);
        _tokenProvider.RefreshTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var worker = CreateWorker(handler);
        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().BeFalse();
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
        result1.Should().BeFalse();

        // Subsequent call must not make an HTTP call
        var result2 = await worker.PollAsync(CancellationToken.None);
        result2.Should().BeFalse();
        await _configManager.DidNotReceive().ApplyConfigAsync(
            Arg.Any<SiteConfig>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── 404 — no config found ─────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_Http404_ReturnsFalse()
    {
        var handler = FakeHandler.RespondStatus(HttpStatusCode.NotFound);
        var worker = CreateWorker(handler);

        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── Network error ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_NetworkError_ReturnsFalseWithoutThrowing()
    {
        var handler = FakeHandler.ThrowNetworkError();
        var worker = CreateWorker(handler);

        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── P2-28: X-Peer-Directory-Version header extraction ───────────────────

    [Fact]
    public async Task PollAsync_WithPeerDirectoryVersionHeader_UpdatesConfigManager()
    {
        _configManager.ApplyConfigAsync(
                Arg.Any<SiteConfig>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConfigApplyResult(ConfigApplyOutcome.Applied, 1, ["initial"], []));
        _configManager.IsPeerDirectoryStale(99).Returns(true);
        _configManager.CurrentPeerDirectoryVersion.Returns(50L);

        var handler = new FakeHandler(_ =>
        {
            var json = BuildSiteConfigJson(configVersion: 1);
            var response = FakeHandler.JsonResponse(json);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"1\"");
            response.Headers.Add("X-Peer-Directory-Version", "99");
            return response;
        });

        var worker = CreateWorker(handler);
        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().BeTrue();
        _configManager.Received().UpdatePeerDirectoryVersion(99);
    }

    [Fact]
    public async Task PollAsync_WithoutPeerDirectoryVersionHeader_DoesNotUpdateVersion()
    {
        _configManager.ApplyConfigAsync(
                Arg.Any<SiteConfig>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConfigApplyResult(ConfigApplyOutcome.Applied, 1, ["initial"], []));

        var worker = CreateWorker(RespondWithConfig(configVersion: 1, etag: "1"));
        var result = await worker.PollAsync(CancellationToken.None);

        result.Should().BeTrue();
        _configManager.DidNotReceive().UpdatePeerDirectoryVersion(Arg.Any<long>());
    }

    // ── ETag extraction from response ─────────────────────────────────────────

    [Fact]
    public async Task PollAsync_ExtractsConfigVersionFromETagHeader()
    {
        _configManager.ApplyConfigAsync(
                Arg.Any<SiteConfig>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConfigApplyResult(ConfigApplyOutcome.Applied, 42, ["initial"], []));

        var worker = CreateWorker(RespondWithConfig(configVersion: 42, etag: "42"));
        await worker.PollAsync(CancellationToken.None);

        await _configManager.Received(1).ApplyConfigAsync(
            Arg.Any<SiteConfig>(), Arg.Any<string>(), Arg.Is("42"), Arg.Any<CancellationToken>());
    }
}

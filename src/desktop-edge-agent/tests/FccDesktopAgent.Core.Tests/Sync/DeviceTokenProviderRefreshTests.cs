using System.Net;
using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Security;
using FccDesktopAgent.Core.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Sync;

/// <summary>
/// Tests for <see cref="DeviceTokenProvider"/> — specifically the atomic token-bundle
/// refresh flow that stages new tokens before promotion and can recover safely if the
/// process is interrupted mid-write.
/// </summary>
public sealed class DeviceTokenProviderRefreshTests
{
    private readonly ICredentialStore _store = Substitute.For<ICredentialStore>();
    private readonly TestHttpHandler _httpHandler = new();
    private readonly IOptions<AgentConfiguration> _config;

    public DeviceTokenProviderRefreshTests()
    {
        _config = Options.Create(new AgentConfiguration
        {
            CloudBaseUrl = "https://api.test.io",
        });
    }

    private DeviceTokenProvider CreateProvider()
    {
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("cloud").Returns(new HttpClient(_httpHandler));

        return new DeviceTokenProvider(
            _store,
            httpFactory,
            _config,
            NullLogger<DeviceTokenProvider>.Instance);
    }

    [Fact]
    public async Task RefreshTokenAsync_SendsRefreshTokenInBody()
    {
        _store.GetSecretAsync(DeviceTokenProvider.TokenBundleKey, Arg.Any<CancellationToken>())
            .Returns(SerializeTokenBundle("current-device-token", "stored-refresh-token"));

        var refreshResponse = new TokenRefreshResponse
        {
            DeviceToken = "new-jwt",
            RefreshToken = "new-refresh",
            TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
        };
        _httpHandler.SetResponse(HttpStatusCode.OK, refreshResponse);

        var provider = CreateProvider();
        await provider.RefreshTokenAsync();

        // Verify the request body contains the refresh token
        _httpHandler.LastRequestBody.Should().Contain("stored-refresh-token");
        _httpHandler.LastRequestBody.Should().Contain("current-device-token");
        // Should NOT have Authorization header (no Bearer auth for refresh)
        _httpHandler.LastRequest!.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task RefreshTokenAsync_RotatesBothTokensAtomically()
    {
        _store.GetSecretAsync(DeviceTokenProvider.TokenBundleKey, Arg.Any<CancellationToken>())
            .Returns(SerializeTokenBundle("old-device-token", "old-refresh"));

        var refreshResponse = new TokenRefreshResponse
        {
            DeviceToken = "new-device-token",
            RefreshToken = "new-refresh-token",
        };
        _httpHandler.SetResponse(HttpStatusCode.OK, refreshResponse);

        var provider = CreateProvider();
        var result = await provider.RefreshTokenAsync();

        result.Should().Be("new-device-token");

        await _store.Received(1).SetSecretAsync(
            DeviceTokenProvider.RefreshPendingKey,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _store.Received(1).SetSecretAsync(
            DeviceTokenProvider.TokenBundleStagingKey,
            Arg.Is<string>(json => json.Contains("new-device-token") && json.Contains("new-refresh-token")),
            Arg.Any<CancellationToken>());
        await _store.Received(1).SetSecretAsync(
            DeviceTokenProvider.TokenBundleKey,
            Arg.Is<string>(json => json.Contains("new-device-token") && json.Contains("new-refresh-token")),
            Arg.Any<CancellationToken>());

        await _store.DidNotReceive().SetSecretAsync(
            DeviceTokenProvider.TokenKey,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _store.DidNotReceive().SetSecretAsync(
            DeviceTokenProvider.RefreshTokenKey,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshTokenAsync_NoRefreshToken_ReturnsNull()
    {
        _store.GetSecretAsync(DeviceTokenProvider.RefreshTokenKey, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var provider = CreateProvider();
        var result = await provider.RefreshTokenAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task RefreshTokenAsync_403_ThrowsDeviceDecommissionedException()
    {
        _store.GetSecretAsync(DeviceTokenProvider.RefreshTokenKey, Arg.Any<CancellationToken>())
            .Returns("some-refresh-token");

        _httpHandler.SetResponse(HttpStatusCode.Forbidden, "{}");

        var provider = CreateProvider();
        var act = () => provider.RefreshTokenAsync();

        await act.Should().ThrowAsync<DeviceDecommissionedException>();
    }

    [Fact]
    public async Task RefreshTokenAsync_401_ThrowsRefreshTokenExpiredException()
    {
        _store.GetSecretAsync(DeviceTokenProvider.TokenBundleKey, Arg.Any<CancellationToken>())
            .Returns(SerializeTokenBundle("expired-device-token", "some-refresh-token"));

        _httpHandler.SetResponse(HttpStatusCode.Unauthorized, "{}");

        var provider = CreateProvider();
        var act = () => provider.RefreshTokenAsync();

        await act.Should().ThrowAsync<RefreshTokenExpiredException>();
    }

    [Fact]
    public async Task StoreTokensAsync_StoresCombinedBundleViaStaging()
    {
        var provider = CreateProvider();

        await provider.StoreTokensAsync("device-jwt", "refresh-opaque");

        await _store.Received(1).SetSecretAsync(
            DeviceTokenProvider.TokenBundleStagingKey,
            Arg.Is<string>(json => json.Contains("device-jwt") && json.Contains("refresh-opaque")),
            Arg.Any<CancellationToken>());
        await _store.Received(1).SetSecretAsync(
            DeviceTokenProvider.TokenBundleKey,
            Arg.Is<string>(json => json.Contains("device-jwt") && json.Contains("refresh-opaque")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshTokenAsync_EmptyCloudBaseUrl_ReturnsNull()
    {
        _store.GetSecretAsync(DeviceTokenProvider.TokenBundleKey, Arg.Any<CancellationToken>())
            .Returns(SerializeTokenBundle("current-device-token", "some-token"));

        var config = Options.Create(new AgentConfiguration { CloudBaseUrl = "" });
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var provider = new DeviceTokenProvider(_store, httpFactory, config, NullLogger<DeviceTokenProvider>.Instance);

        var result = await provider.RefreshTokenAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task RefreshTokenAsync_WithRecoverableStagedBundle_ReturnsRecoveredTokenWithoutCallingHttp()
    {
        _store.GetSecretAsync(DeviceTokenProvider.TokenBundleStagingKey, Arg.Any<CancellationToken>())
            .Returns(SerializeTokenBundle("recovered-device-token", "recovered-refresh-token"));

        var provider = CreateProvider();
        var result = await provider.RefreshTokenAsync();

        result.Should().Be("recovered-device-token");
        _httpHandler.LastRequest.Should().BeNull();
        await _store.Received(1).SetSecretAsync(
            DeviceTokenProvider.TokenBundleKey,
            Arg.Is<string>(json => json.Contains("recovered-device-token") && json.Contains("recovered-refresh-token")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshTokenAsync_WithPendingMarkerWithoutStaging_ThrowsRefreshTokenExpiredException()
    {
        _store.GetSecretAsync(DeviceTokenProvider.RefreshPendingKey, Arg.Any<CancellationToken>())
            .Returns("2026-03-13T00:00:00.0000000+00:00");
        _store.GetSecretAsync(DeviceTokenProvider.TokenBundleKey, Arg.Any<CancellationToken>())
            .Returns(SerializeTokenBundle("old-device-token", "old-refresh-token"));

        var provider = CreateProvider();
        var act = () => provider.RefreshTokenAsync();

        await act.Should().ThrowAsync<RefreshTokenExpiredException>();
        _httpHandler.LastRequest.Should().BeNull();
    }

    private sealed class TestHttpHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode;
        private string _body = "{}";

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public void SetResponse<T>(HttpStatusCode statusCode, T body)
        {
            _statusCode = statusCode;
            _body = JsonSerializer.Serialize(body);
        }

        public void SetResponse(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string SerializeTokenBundle(string deviceToken, string refreshToken) =>
        JsonSerializer.Serialize(new
        {
            DeviceToken = deviceToken,
            RefreshToken = refreshToken
        });
}

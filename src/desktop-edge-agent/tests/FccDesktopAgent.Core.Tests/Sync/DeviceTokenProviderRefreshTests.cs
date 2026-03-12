using System.Net;
using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Security;
using FccDesktopAgent.Core.Sync;
using FccDesktopAgent.Core.Sync.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Sync;

/// <summary>
/// Tests for <see cref="DeviceTokenProvider"/> — specifically the corrected refresh flow
/// that sends the refresh token in the request body (NOT as Bearer header) per spec,
/// and rotates both tokens on success.
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
        _store.GetSecretAsync(DeviceTokenProvider.RefreshTokenKey, Arg.Any<CancellationToken>())
            .Returns("stored-refresh-token");

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
        // Should NOT have Authorization header (no Bearer auth for refresh)
        _httpHandler.LastRequest!.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task RefreshTokenAsync_RotatesBothTokens()
    {
        _store.GetSecretAsync(DeviceTokenProvider.RefreshTokenKey, Arg.Any<CancellationToken>())
            .Returns("old-refresh");

        var refreshResponse = new TokenRefreshResponse
        {
            DeviceToken = "new-device-token",
            RefreshToken = "new-refresh-token",
        };
        _httpHandler.SetResponse(HttpStatusCode.OK, refreshResponse);

        var provider = CreateProvider();
        var result = await provider.RefreshTokenAsync();

        result.Should().Be("new-device-token");

        // Verify both tokens were stored
        await _store.Received(1).SetSecretAsync(DeviceTokenProvider.TokenKey, "new-device-token", Arg.Any<CancellationToken>());
        await _store.Received(1).SetSecretAsync(DeviceTokenProvider.RefreshTokenKey, "new-refresh-token", Arg.Any<CancellationToken>());
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
    public async Task RefreshTokenAsync_401_ReturnsNull()
    {
        _store.GetSecretAsync(DeviceTokenProvider.RefreshTokenKey, Arg.Any<CancellationToken>())
            .Returns("some-refresh-token");

        _httpHandler.SetResponse(HttpStatusCode.Unauthorized, "{}");

        var provider = CreateProvider();
        var result = await provider.RefreshTokenAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task StoreTokensAsync_StoresBothKeys()
    {
        var provider = CreateProvider();

        await provider.StoreTokensAsync("device-jwt", "refresh-opaque");

        await _store.Received(1).SetSecretAsync(DeviceTokenProvider.TokenKey, "device-jwt", Arg.Any<CancellationToken>());
        await _store.Received(1).SetSecretAsync(DeviceTokenProvider.RefreshTokenKey, "refresh-opaque", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshTokenAsync_EmptyCloudBaseUrl_ReturnsNull()
    {
        _store.GetSecretAsync(DeviceTokenProvider.RefreshTokenKey, Arg.Any<CancellationToken>())
            .Returns("some-token");

        var config = Options.Create(new AgentConfiguration { CloudBaseUrl = "" });
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var provider = new DeviceTokenProvider(_store, httpFactory, config, NullLogger<DeviceTokenProvider>.Instance);

        var result = await provider.RefreshTokenAsync();
        result.Should().BeNull();
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
}

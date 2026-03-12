using System.Net;
using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Registration;

/// <summary>
/// Tests for <see cref="DeviceRegistrationService"/>.
/// Uses a mock HTTP handler to simulate cloud responses.
/// </summary>
public sealed class DeviceRegistrationServiceTests
{
    private const string CloudUrl = "https://api.test.io";

    private readonly TestHttpHandler _httpHandler = new();
    private readonly IDeviceTokenProvider _tokenProvider = Substitute.For<IDeviceTokenProvider>();
    private readonly IRegistrationManager _registrationManager = Substitute.For<IRegistrationManager>();
    private readonly IConfigManager _configManager = Substitute.For<IConfigManager>();

    private DeviceRegistrationService CreateService()
    {
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("cloud").Returns(new HttpClient(_httpHandler));

        return new DeviceRegistrationService(
            httpFactory,
            _tokenProvider,
            _registrationManager,
            _configManager,
            NullLogger<DeviceRegistrationService>.Instance);
    }

    private static DeviceRegistrationRequest CreateRequest() => new()
    {
        ProvisioningToken = "test-token",
        SiteCode = "SITE-001",
        DeviceSerialNumber = "SN-123",
        DeviceModel = "win-x64",
        OsVersion = "Windows 11",
        AgentVersion = "1.0.0",
    };

    [Fact]
    public async Task RegisterAsync_Success_StoresTokensAndIdentity()
    {
        var response = new DeviceRegistrationResponse
        {
            DeviceId = "device-abc",
            DeviceToken = "jwt-token",
            RefreshToken = "refresh-token",
            TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            SiteCode = "SITE-001",
            LegalEntityId = "le-123",
            RegisteredAt = DateTimeOffset.UtcNow,
        };

        _httpHandler.SetResponse(HttpStatusCode.Created, response);
        _registrationManager.LoadState().Returns(new RegistrationState());

        var service = CreateService();
        var result = await service.RegisterAsync(CloudUrl, CreateRequest());

        result.Should().BeOfType<RegistrationResult.Success>();
        var success = (RegistrationResult.Success)result;
        success.Response.DeviceId.Should().Be("device-abc");

        // Verify tokens were stored
        await _tokenProvider.Received(1).StoreTokensAsync("jwt-token", "refresh-token", Arg.Any<CancellationToken>());

        // Verify identity was persisted
        await _registrationManager.Received(1).SaveStateAsync(
            Arg.Is<RegistrationState>(s =>
                s.IsRegistered &&
                s.DeviceId == "device-abc" &&
                s.SiteCode == "SITE-001" &&
                s.CloudBaseUrl == CloudUrl),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_Success_WithSiteConfig_AppliesConfig()
    {
        var siteConfig = new SiteConfig
        {
            ConfigVersion = 1,
            ConfigId = "cfg-1",
        };
        var response = new DeviceRegistrationResponse
        {
            DeviceId = "device-abc",
            DeviceToken = "jwt-token",
            RefreshToken = "refresh-token",
            SiteCode = "SITE-001",
            LegalEntityId = "le-123",
            RegisteredAt = DateTimeOffset.UtcNow,
            SiteConfig = siteConfig,
        };

        _httpHandler.SetResponse(HttpStatusCode.Created, response);
        _registrationManager.LoadState().Returns(new RegistrationState());

        var service = CreateService();
        await service.RegisterAsync(CloudUrl, CreateRequest());

        // Verify config was applied
        await _configManager.Received(1).ApplyConfigAsync(
            Arg.Any<SiteConfig>(),
            Arg.Any<string>(),
            "1",
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("BOOTSTRAP_TOKEN_INVALID", RegistrationErrorCode.BootstrapTokenInvalid)]
    [InlineData("BOOTSTRAP_TOKEN_EXPIRED", RegistrationErrorCode.BootstrapTokenExpired)]
    [InlineData("BOOTSTRAP_TOKEN_ALREADY_USED", RegistrationErrorCode.BootstrapTokenAlreadyUsed)]
    [InlineData("ACTIVE_AGENT_EXISTS", RegistrationErrorCode.ActiveAgentExists)]
    [InlineData("SITE_NOT_FOUND", RegistrationErrorCode.SiteNotFound)]
    [InlineData("SITE_MISMATCH", RegistrationErrorCode.SiteMismatch)]
    public async Task RegisterAsync_Rejected_ReturnsCorrectErrorCode(string errorCode, RegistrationErrorCode expected)
    {
        var errorResponse = new RegistrationErrorResponse
        {
            ErrorCode = errorCode,
            Message = "Test error",
        };
        _httpHandler.SetResponse(HttpStatusCode.BadRequest, errorResponse);

        var service = CreateService();
        var result = await service.RegisterAsync(CloudUrl, CreateRequest());

        result.Should().BeOfType<RegistrationResult.Rejected>();
        var rejected = (RegistrationResult.Rejected)result;
        rejected.Code.Should().Be(expected);
        rejected.Message.Should().Be("Test error");
    }

    [Fact]
    public async Task RegisterAsync_NetworkError_ReturnsTransportError()
    {
        _httpHandler.SetException(new HttpRequestException("Connection refused"));

        var service = CreateService();
        var result = await service.RegisterAsync(CloudUrl, CreateRequest());

        result.Should().BeOfType<RegistrationResult.TransportError>();
        var error = (RegistrationResult.TransportError)result;
        error.Message.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task RegisterAsync_ServerError_ReturnsTransportError()
    {
        _httpHandler.SetResponse(HttpStatusCode.InternalServerError, "Server error");

        var service = CreateService();
        var result = await service.RegisterAsync(CloudUrl, CreateRequest());

        result.Should().BeOfType<RegistrationResult.TransportError>();
    }

    /// <summary>Simple test HTTP handler that returns a pre-configured response.</summary>
    private sealed class TestHttpHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode;
        private string _body = "{}";
        private Exception? _exception;

        public void SetResponse<T>(HttpStatusCode statusCode, T body)
        {
            _statusCode = statusCode;
            _body = JsonSerializer.Serialize(body);
            _exception = null;
        }

        public void SetResponse(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
            _exception = null;
        }

        public void SetException(Exception ex)
        {
            _exception = ex;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_exception is not null)
                throw _exception;

            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}

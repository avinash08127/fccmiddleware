using System.Reflection;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Security;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.InMemory;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Security;

/// <summary>
/// DEA-6.2: Security hardening verification tests.
/// Validates constant-time comparison, log redaction, HTTPS enforcement,
/// and [SensitiveData] attribute coverage.
/// </summary>
public sealed class SecurityHardeningTests
{
    // ── [SensitiveData] attribute coverage ──────────────────────────────────

    [Fact]
    public void AgentConfiguration_FccApiKey_HasSensitiveDataAttribute()
    {
        var prop = typeof(AgentConfiguration).GetProperty(nameof(AgentConfiguration.FccApiKey));
        prop.Should().NotBeNull();
        prop!.GetCustomAttribute<SensitiveDataAttribute>().Should().NotBeNull(
            "FccApiKey must be marked [SensitiveData] to prevent accidental logging");
    }

    [Fact]
    public void FccConnectionConfig_ApiKey_HasSensitiveDataAttribute()
    {
        var prop = typeof(FccConnectionConfig).GetProperty(nameof(FccConnectionConfig.ApiKey));
        prop.Should().NotBeNull();
        prop!.GetCustomAttribute<SensitiveDataAttribute>().Should().NotBeNull(
            "FccConnectionConfig.ApiKey must be marked [SensitiveData]");
    }

    // ── Serilog destructuring policy ────────────────────────────────────────

    [Fact]
    public void SensitiveDataDestructuringPolicy_RedactsSensitiveFields()
    {
        var inMemorySink = new InMemorySink();
        var logger = new LoggerConfiguration()
            .Destructure.With<SensitiveDataDestructuringPolicy>()
            .WriteTo.Sink(inMemorySink)
            .CreateLogger();

        var config = new AgentConfiguration
        {
            DeviceId = "test-device-123",
            FccApiKey = "super-secret-api-key-should-not-appear",
            FccBaseUrl = "http://192.168.1.100:8080"
        };

        logger.Information("Config: {@Config}", config);

        var logEvent = inMemorySink.LogEvents.Should().ContainSingle().Subject;
        var rendered = logEvent.RenderMessage();

        rendered.Should().NotContain("super-secret-api-key-should-not-appear",
            "FccApiKey is marked [SensitiveData] and must be redacted in logs");
        rendered.Should().Contain("[REDACTED]");
        rendered.Should().Contain("test-device-123",
            "Non-sensitive fields should still be visible");
    }

    [Fact]
    public void SensitiveDataDestructuringPolicy_PassesThroughNonSensitiveTypes()
    {
        var policy = new SensitiveDataDestructuringPolicy();
        var factory = new Serilog.Parsing.MessageTemplateParser();

        // An anonymous type with no [SensitiveData] should not be handled
        var plainObject = new { Name = "test", Value = 42 };
        var handled = policy.TryDestructure(
            plainObject,
            new ScalarPropertyValueFactory(),
            out _);

        handled.Should().BeFalse("types without [SensitiveData] should be left to default destructuring");
    }

    // ── Cloud URL guard ────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://api.fcc-middleware.prod.example.com", true)]
    [InlineData("https://api.fcc-middleware.staging.example.com/v1", true)]
    [InlineData("http://localhost:5000", true)]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://[::1]:5000", true)]
    [InlineData("http://api.example.com", false)]
    [InlineData("http://192.168.1.100:8080", false)]
    [InlineData("ftp://api.example.com", false)]
    [InlineData("", false)]
    [InlineData("not-a-url", false)]
    public void CloudUrlGuard_EnforcesHttps(string url, bool expectedSecure)
    {
        CloudUrlGuard.IsSecure(url).Should().Be(expectedSecure);
    }

    // ── Constant-time comparison ────────────────────────────────────────────

    [Fact]
    public void ConstantTimeEquals_MatchingKeys_ReturnsTrue()
    {
        var key = "a1b2c3d4e5f6g7h8i9j0";
        FccDesktopAgent.Api.Auth.ApiKeyMiddleware
            .ConstantTimeEquals(key, key).Should().BeTrue();
    }

    [Fact]
    public void ConstantTimeEquals_DifferentKeys_ReturnsFalse()
    {
        FccDesktopAgent.Api.Auth.ApiKeyMiddleware
            .ConstantTimeEquals("correct-key", "wrong-key").Should().BeFalse();
    }

    [Fact]
    public void ConstantTimeEquals_DifferentLengths_ReturnsFalse()
    {
        FccDesktopAgent.Api.Auth.ApiKeyMiddleware
            .ConstantTimeEquals("short", "much-longer-key").Should().BeFalse();
    }

    [Fact]
    public void ConstantTimeEquals_EmptyStrings_ReturnsTrue()
    {
        FccDesktopAgent.Api.Auth.ApiKeyMiddleware
            .ConstantTimeEquals("", "").Should().BeTrue();
    }

    // ── Credential key constants ────────────────────────────────────────────

    [Fact]
    public void CredentialKeys_AreNonEmpty()
    {
        CredentialKeys.DeviceToken.Should().NotBeNullOrWhiteSpace();
        CredentialKeys.RefreshToken.Should().NotBeNullOrWhiteSpace();
        CredentialKeys.FccApiKey.Should().NotBeNullOrWhiteSpace();
        CredentialKeys.LanApiKey.Should().NotBeNullOrWhiteSpace();
    }

    // ── Minimal ILogEventPropertyValueFactory for test ──────────────────────

    private sealed class ScalarPropertyValueFactory : ILogEventPropertyValueFactory
    {
        public LogEventPropertyValue CreatePropertyValue(object? value, bool destructureObjects = false)
            => new ScalarValue(value);
    }
}

using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Registration;

/// <summary>
/// Tests for <see cref="RegistrationManager"/>.
/// Uses a temp directory to isolate file I/O from real agent data.
/// </summary>
public sealed class RegistrationManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RegistrationManager _manager;

    public RegistrationManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fcc-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _manager = new RegistrationManager(NullLogger<RegistrationManager>.Instance, _tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void LoadState_NoFile_ReturnsDefaultUnregisteredState()
    {
        // A fresh manager with no file on disk should return an unregistered state
        var state = _manager.LoadState();

        state.IsRegistered.Should().BeFalse();
        state.IsDecommissioned.Should().BeFalse();
        state.DeviceId.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var state = new RegistrationState
        {
            IsRegistered = true,
            DeviceId = "test-device-123",
            SiteCode = "SITE-001",
            LegalEntityId = "le-456",
            CloudBaseUrl = "https://api.test.io",
            RegisteredAt = DateTimeOffset.UtcNow,
            DeviceSerialNumber = "SN-789",
            DeviceModel = "win-x64",
            OsVersion = "Windows 11",
            AgentVersion = "1.0.0",
        };

        await _manager.SaveStateAsync(state);

        // Create a new manager to force re-read from disk
        var manager2 = new RegistrationManager(NullLogger<RegistrationManager>.Instance, _tempDir);
        var loaded = manager2.LoadState();

        loaded.IsRegistered.Should().BeTrue();
        loaded.DeviceId.Should().Be("test-device-123");
        loaded.SiteCode.Should().Be("SITE-001");
        loaded.LegalEntityId.Should().Be("le-456");
        loaded.CloudBaseUrl.Should().Be("https://api.test.io");
    }

    [Fact]
    public async Task MarkDecommissioned_SetsFlag()
    {
        var state = new RegistrationState
        {
            IsRegistered = true,
            DeviceId = "device-to-decommission",
            SiteCode = "SITE-001",
        };
        await _manager.SaveStateAsync(state);

        await _manager.MarkDecommissionedAsync();

        var loaded = _manager.LoadState();
        loaded.IsDecommissioned.Should().BeTrue();
        loaded.IsRegistered.Should().BeTrue(); // still registered, just decommissioned
    }

    [Fact]
    public async Task PostConfigure_RegisteredState_OverlaysValues()
    {
        // Pre-populate the cached state
        var state = new RegistrationState
        {
            IsRegistered = true,
            DeviceId = "device-abc",
            SiteCode = "SITE-XYZ",
            CloudBaseUrl = "https://cloud.test.io",
        };
        // Use SaveStateAsync to cache the state
        await _manager.SaveStateAsync(state);

        var config = new AgentConfiguration();
        ((IPostConfigureOptions<AgentConfiguration>)_manager).PostConfigure(Options.DefaultName, config);

        config.DeviceId.Should().Be("device-abc");
        config.SiteId.Should().Be("SITE-XYZ");
        config.CloudBaseUrl.Should().Be("https://cloud.test.io");
    }

    [Fact]
    public void PostConfigure_UnregisteredState_DoesNotOverlay()
    {
        var config = new AgentConfiguration
        {
            DeviceId = "original",
            SiteId = "original-site",
            CloudBaseUrl = "https://original.io",
        };

        ((IPostConfigureOptions<AgentConfiguration>)_manager).PostConfigure(Options.DefaultName, config);

        config.DeviceId.Should().Be("original");
        config.SiteId.Should().Be("original-site");
        config.CloudBaseUrl.Should().Be("https://original.io");
    }

    [Fact]
    public void LoadState_CachesResult()
    {
        var state1 = _manager.LoadState();
        var state2 = _manager.LoadState();

        // Should return the same cached instance
        ReferenceEquals(state1, state2).Should().BeTrue();
    }
}

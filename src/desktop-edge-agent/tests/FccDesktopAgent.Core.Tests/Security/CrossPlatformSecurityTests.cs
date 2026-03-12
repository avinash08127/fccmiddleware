using System.Runtime.InteropServices;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Security;

/// <summary>
/// DEA-6.3: Cross-platform verification tests for security features.
/// These tests validate that platform-specific security mechanisms work correctly
/// on the current OS. The CI matrix should run these on Windows, macOS, and Linux.
/// </summary>
public sealed class CrossPlatformSecurityTests : IAsyncLifetime
{
    private readonly PlatformCredentialStore _store;
    private readonly string _testPrefix = $"xplat-test:{Guid.NewGuid():N}:";

    public CrossPlatformSecurityTests()
    {
        _store = new PlatformCredentialStore(NullLogger<PlatformCredentialStore>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _store.DeleteSecretAsync($"{_testPrefix}token");
        await _store.DeleteSecretAsync($"{_testPrefix}fcc-api-key");
        await _store.DeleteSecretAsync($"{_testPrefix}lan-api-key");
    }

    // ── Credential store platform verification ──────────────────────────────

    [Fact]
    public async Task CredentialStore_StoresAndRetrievesDeviceToken()
    {
        var key = $"{_testPrefix}token";
        var token = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.test-device-jwt";

        await _store.SetSecretAsync(key, token);
        var retrieved = await _store.GetSecretAsync(key);

        retrieved.Should().Be(token, "device token must round-trip through platform credential store");
    }

    [Fact]
    public async Task CredentialStore_StoresAndRetrievesFccApiKey()
    {
        var key = $"{_testPrefix}fcc-api-key";
        var apiKey = Convert.ToBase64String(new byte[32]); // 256-bit key

        await _store.SetSecretAsync(key, apiKey);
        var retrieved = await _store.GetSecretAsync(key);

        retrieved.Should().Be(apiKey, "FCC API key must round-trip through platform credential store");
    }

    [Fact]
    public async Task CredentialStore_StoresAndRetrievesLanApiKey()
    {
        var key = $"{_testPrefix}lan-api-key";
        var lanKey = Convert.ToBase64String(new byte[32]); // 256-bit key

        await _store.SetSecretAsync(key, lanKey);
        var retrieved = await _store.GetSecretAsync(key);

        retrieved.Should().Be(lanKey, "LAN API key must round-trip through platform credential store");
    }

    [Fact]
    public async Task CredentialStore_DeletedKeyReturnsNull()
    {
        var key = $"{_testPrefix}token";
        await _store.SetSecretAsync(key, "to-be-deleted");
        await _store.DeleteSecretAsync(key);

        var result = await _store.GetSecretAsync(key);
        result.Should().BeNull("deleted credentials must not be recoverable");
    }

    // ── Data directory verification ─────────────────────────────────────────

    [Fact]
    public void DataDirectory_ResolvesPlatformAppropriately()
    {
        var dir = AgentDataDirectory.Resolve();

        dir.Should().NotBeNullOrWhiteSpace();
        Directory.Exists(dir).Should().BeTrue("data directory should be created");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            dir.Should().Contain("FccDesktopAgent");
            dir.Should().Contain("AppData");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            dir.Should().Contain("FccDesktopAgent");
            dir.Should().Contain("Application Support");
        }
        else
        {
            dir.Should().Contain("FccDesktopAgent");
        }
    }

    [Fact]
    public void DataDirectory_HasRestrictivePermissionsOnUnix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Windows uses per-user %LOCALAPPDATA%; permissions are inherited

        var dir = AgentDataDirectory.Resolve();
        var dirInfo = new DirectoryInfo(dir);

        // On Unix: should be rwx------ (0700)
        dirInfo.UnixFileMode.Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            "data directory must be owner-only (chmod 700) to prevent other users from accessing the database");
    }

    // ── Platform detection ──────────────────────────────────────────────────

    [Fact]
    public void PlatformDetection_IdentifiesCurrentOS()
    {
        // At least one of these must be true
        var isKnownPlatform =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        isKnownPlatform.Should().BeTrue("agent must run on a supported platform");
    }

    [Fact]
    public void DatabasePath_IsUnderDataDirectory()
    {
        var dbPath = AgentDataDirectory.GetDatabasePath();
        var dataDir = AgentDataDirectory.Resolve();

        dbPath.Should().StartWith(dataDir);
        dbPath.Should().EndWith("agent.db");
    }
}

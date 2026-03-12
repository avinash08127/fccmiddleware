using FccDesktopAgent.Core.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Security;

/// <summary>
/// Tests for <see cref="PlatformCredentialStore"/>.
/// These tests exercise the platform-specific credential store on the current OS.
/// </summary>
public sealed class PlatformCredentialStoreTests : IDisposable
{
    private readonly PlatformCredentialStore _store;

    // Use a unique prefix to avoid collisions with real credentials
    private readonly string _testPrefix = $"test:{Guid.NewGuid():N}:";

    public PlatformCredentialStoreTests()
    {
        _store = new PlatformCredentialStore(NullLogger<PlatformCredentialStore>.Instance);
    }

    public void Dispose()
    {
        // Best-effort cleanup of test keys
        _store.DeleteSecretAsync($"{_testPrefix}key1").GetAwaiter().GetResult();
        _store.DeleteSecretAsync($"{_testPrefix}key2").GetAwaiter().GetResult();
        _store.DeleteSecretAsync($"{_testPrefix}overwrite").GetAwaiter().GetResult();
    }

    [Fact]
    public async Task SetAndGet_RoundTrips()
    {
        var key = $"{_testPrefix}key1";
        var secret = "my-secret-value-" + Guid.NewGuid();

        await _store.SetSecretAsync(key, secret);
        var retrieved = await _store.GetSecretAsync(key);

        retrieved.Should().Be(secret);
    }

    [Fact]
    public async Task Get_NonExistentKey_ReturnsNull()
    {
        var result = await _store.GetSecretAsync($"{_testPrefix}nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Set_Overwrite_ReturnsNewValue()
    {
        var key = $"{_testPrefix}overwrite";

        await _store.SetSecretAsync(key, "value1");
        await _store.SetSecretAsync(key, "value2");

        var retrieved = await _store.GetSecretAsync(key);
        retrieved.Should().Be("value2");
    }

    [Fact]
    public async Task Delete_RemovesKey()
    {
        var key = $"{_testPrefix}key2";
        await _store.SetSecretAsync(key, "to-delete");

        await _store.DeleteSecretAsync(key);

        var retrieved = await _store.GetSecretAsync(key);
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task Delete_NonExistentKey_IsNoOp()
    {
        // Should not throw
        await _store.DeleteSecretAsync($"{_testPrefix}nonexistent");
    }

    [Fact]
    public async Task ConcurrentAccess_DoesNotCorrupt()
    {
        var key = $"{_testPrefix}key1";
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            var value = $"value-{i}";
            tasks.Add(_store.SetSecretAsync(key, value));
        }

        await Task.WhenAll(tasks);

        var result = await _store.GetSecretAsync(key);
        result.Should().NotBeNull();
        result.Should().StartWith("value-");
    }

    [Fact]
    public async Task LongSecret_RoundTrips()
    {
        var key = $"{_testPrefix}key1";
        var secret = new string('x', 4096);

        await _store.SetSecretAsync(key, secret);
        var retrieved = await _store.GetSecretAsync(key);

        retrieved.Should().Be(secret);
    }

    [Fact]
    public async Task SpecialCharacters_RoundTrips()
    {
        var key = $"{_testPrefix}key1";
        var secret = "p@$$w0rd!#%&*(){}[]|\\:\";<>,.?/~`";

        await _store.SetSecretAsync(key, secret);
        var retrieved = await _store.GetSecretAsync(key);

        retrieved.Should().Be(secret);
    }
}

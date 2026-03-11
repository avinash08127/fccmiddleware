namespace FccDesktopAgent.Core.Security;

/// <summary>
/// Platform-agnostic secure credential storage.
/// Implementations: DPAPI (Windows), Keychain (macOS), libsecret (Linux).
/// NEVER store FCC credentials, tokens, or customer TIN in plain text or logs.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Store a secret under the given key. Overwrites existing value.</summary>
    Task SetSecretAsync(string key, string secret, CancellationToken ct = default);

    /// <summary>Retrieve a secret by key. Returns null if not found.</summary>
    Task<string?> GetSecretAsync(string key, CancellationToken ct = default);

    /// <summary>Delete a secret by key. No-op if key does not exist.</summary>
    Task DeleteSecretAsync(string key, CancellationToken ct = default);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Buffer;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Security;

/// <summary>
/// Cross-platform secure credential storage.
///   Windows: DPAPI via <see cref="ProtectedData"/> (CurrentUser scope)
///   macOS:   Keychain via <c>/usr/bin/security</c> CLI
///   Linux:   <c>secret-tool</c> (libsecret) with AES-256 encrypted file fallback
///
/// Architecture rule #9: NEVER log secret values.
/// Architecture rule #15: All platform-specific code behind an abstraction.
/// </summary>
public sealed class PlatformCredentialStore : ICredentialStore
{
    private const string ServiceName = "FccDesktopAgent";

    private readonly ILogger<PlatformCredentialStore> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    // Lazy-evaluated Linux fallback flag
    private bool? _linuxHasSecretTool;

    public PlatformCredentialStore(ILogger<PlatformCredentialStore> logger)
    {
        _logger = logger;
    }

    public async Task SetSecretAsync(string key, string secret, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(secret);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            await SetSecretWindowsAsync(key, secret, ct);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            await SetSecretMacOsAsync(key, secret, ct);
        else
            await SetSecretLinuxAsync(key, secret, ct);

        _logger.LogDebug("Stored secret under key {Key}", key);
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await GetSecretWindowsAsync(key, ct);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return await GetSecretMacOsAsync(key, ct);
        return await GetSecretLinuxAsync(key, ct);
    }

    public async Task DeleteSecretAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            await DeleteSecretWindowsAsync(key, ct);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            await DeleteSecretMacOsAsync(key, ct);
        else
            await DeleteSecretLinuxAsync(key, ct);

        _logger.LogDebug("Deleted secret under key {Key}", key);
    }

    // ── Windows (DPAPI) ──────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private async Task SetSecretWindowsAsync(string key, string secret, CancellationToken ct)
    {
        var plainBytes = Encoding.UTF8.GetBytes(secret);
        var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        var base64 = Convert.ToBase64String(encrypted);

        await _fileLock.WaitAsync(ct);
        try
        {
            var store = await LoadFileStoreAsync(ct);
            store[key] = base64;
            await SaveFileStoreAsync(store, ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<string?> GetSecretWindowsAsync(string key, CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            var store = await LoadFileStoreAsync(ct);
            if (!store.TryGetValue(key, out var base64))
                return null;

            var encrypted = Convert.FromBase64String(base64);
            var plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt secret for key {Key} — credential may be corrupted", key);
            return null;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task DeleteSecretWindowsAsync(string key, CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            var store = await LoadFileStoreAsync(ct);
            if (store.Remove(key))
                await SaveFileStoreAsync(store, ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    // ── macOS (Keychain via security CLI) ────────────────────────────────────

    private async Task SetSecretMacOsAsync(string key, string secret, CancellationToken ct)
    {
        // -U flag updates if exists, otherwise adds
        var (exitCode, _, stderr) = await RunProcessAsync(
            "/usr/bin/security",
            ["add-generic-password", "-a", key, "-s", ServiceName, "-w", secret, "-U"],
            ct);

        if (exitCode != 0)
            _logger.LogWarning("Keychain set for key {Key} returned exit code {ExitCode}: {Stderr}",
                key, exitCode, stderr);
    }

    private async Task<string?> GetSecretMacOsAsync(string key, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await RunProcessAsync(
            "/usr/bin/security",
            ["find-generic-password", "-a", key, "-s", ServiceName, "-w"],
            ct);

        return exitCode == 0 ? stdout.Trim() : null;
    }

    private async Task DeleteSecretMacOsAsync(string key, CancellationToken ct)
    {
        var (exitCode, _, stderr) = await RunProcessAsync(
            "/usr/bin/security",
            ["delete-generic-password", "-a", key, "-s", ServiceName],
            ct);

        if (exitCode != 0 && !stderr.Contains("could not be found", StringComparison.OrdinalIgnoreCase))
            _logger.LogWarning("Keychain delete for key {Key} returned exit code {ExitCode}", key, exitCode);
    }

    // ── Linux (secret-tool / AES file fallback) ─────────────────────────────

    private async Task SetSecretLinuxAsync(string key, string secret, CancellationToken ct)
    {
        if (await HasSecretToolAsync(ct))
        {
            // secret-tool reads the secret from stdin
            var (exitCode, _, stderr) = await RunProcessAsync(
                "secret-tool",
                ["store", "--label", $"{ServiceName} {key}", "service", ServiceName, "key", key],
                ct,
                stdinData: secret);

            if (exitCode == 0) return;

            _logger.LogWarning("secret-tool store failed ({ExitCode}), falling back to encrypted file: {Stderr}",
                exitCode, stderr);
        }

        await SetSecretEncryptedFileAsync(key, secret, ct);
    }

    private async Task<string?> GetSecretLinuxAsync(string key, CancellationToken ct)
    {
        if (await HasSecretToolAsync(ct))
        {
            var (exitCode, stdout, _) = await RunProcessAsync(
                "secret-tool",
                ["lookup", "service", ServiceName, "key", key],
                ct);

            if (exitCode == 0 && !string.IsNullOrEmpty(stdout))
                return stdout.Trim();
        }

        return await GetSecretEncryptedFileAsync(key, ct);
    }

    private async Task DeleteSecretLinuxAsync(string key, CancellationToken ct)
    {
        if (await HasSecretToolAsync(ct))
        {
            await RunProcessAsync(
                "secret-tool",
                ["clear", "service", ServiceName, "key", key],
                ct);
        }

        // Also clear from file fallback
        await DeleteSecretEncryptedFileAsync(key, ct);
    }

    // ── Linux AES-256 encrypted file fallback ────────────────────────────────

    private async Task SetSecretEncryptedFileAsync(string key, string secret, CancellationToken ct)
    {
        var derivedKey = DeriveLinuxMachineKey();
        var plainBytes = Encoding.UTF8.GetBytes(secret);

        using var aes = Aes.Create();
        aes.Key = derivedKey;
        aes.GenerateIV();

        using var ms = new MemoryStream();
        // Write IV first (16 bytes for AES)
        ms.Write(aes.IV);
        using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
        {
            cs.Write(plainBytes);
            cs.FlushFinalBlock();
        }
        var base64 = Convert.ToBase64String(ms.ToArray());

        await _fileLock.WaitAsync(ct);
        try
        {
            var store = await LoadFileStoreAsync(ct);
            store[key] = base64;
            await SaveFileStoreAsync(store, ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<string?> GetSecretEncryptedFileAsync(string key, CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            var store = await LoadFileStoreAsync(ct);
            if (!store.TryGetValue(key, out var base64))
                return null;

            var derivedKey = DeriveLinuxMachineKey();
            var combined = Convert.FromBase64String(base64);

            // First 16 bytes are IV
            if (combined.Length < 17) return null;
            var iv = combined[..16];
            var ciphertext = combined[16..];

            using var aes = Aes.Create();
            aes.Key = derivedKey;
            aes.IV = iv;

            using var ms = new MemoryStream(ciphertext);
            using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var reader = new StreamReader(cs, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt secret for key {Key} — credential may be corrupted", key);
            return null;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task DeleteSecretEncryptedFileAsync(string key, CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            var store = await LoadFileStoreAsync(ct);
            if (store.Remove(key))
                await SaveFileStoreAsync(store, ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Derives a 256-bit AES key from the Linux machine-id.
    /// Used as fallback when secret-tool (libsecret) is unavailable.
    /// </summary>
    private static byte[] DeriveLinuxMachineKey()
    {
        var machineId = "fcc-desktop-agent-fallback";
        try
        {
            var machineIdPath = "/etc/machine-id";
            if (File.Exists(machineIdPath))
                machineId = File.ReadAllText(machineIdPath).Trim();
        }
        catch
        {
            // Fallback to hostname if machine-id not readable
            machineId = Environment.MachineName;
        }

        var salt = Encoding.UTF8.GetBytes("FccDesktopAgent.CredentialStore.v1");
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(machineId),
            salt,
            iterations: 100_000,
            HashAlgorithmName.SHA256,
            outputLength: 32);
    }

    private async Task<bool> HasSecretToolAsync(CancellationToken ct)
    {
        if (_linuxHasSecretTool.HasValue)
            return _linuxHasSecretTool.Value;

        try
        {
            var (exitCode, _, _) = await RunProcessAsync("which", ["secret-tool"], ct);
            _linuxHasSecretTool = exitCode == 0;
        }
        catch
        {
            _linuxHasSecretTool = false;
        }

        if (!_linuxHasSecretTool.Value)
            _logger.LogInformation("secret-tool not available — using encrypted file fallback for credential storage");

        return _linuxHasSecretTool.Value;
    }

    // ── File-based store helpers (Windows DPAPI + Linux AES fallback) ────────

    private static string GetStoreFilePath()
    {
        var dir = Path.Combine(AgentDataDirectory.Resolve(), "secrets");
        Directory.CreateDirectory(dir);
        // DEA-6.2: Restrictive permissions on secrets directory (owner-only)
        AgentDataDirectory.SetRestrictivePermissions(dir);
        return Path.Combine(dir, "credentials.dat");
    }

    private static async Task<Dictionary<string, string>> LoadFileStoreAsync(CancellationToken ct)
    {
        var path = GetStoreFilePath();
        if (!File.Exists(path))
            return new Dictionary<string, string>();

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
               ?? new Dictionary<string, string>();
    }

    private static async Task SaveFileStoreAsync(Dictionary<string, string> store, CancellationToken ct)
    {
        var path = GetStoreFilePath();
        var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(path, json, ct);
    }

    // ── Process execution helper ─────────────────────────────────────────────

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string[] arguments, CancellationToken ct, string? stdinData = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinData is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();

        if (stdinData is not null)
        {
            await process.StandardInput.WriteAsync(stdinData);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }
}

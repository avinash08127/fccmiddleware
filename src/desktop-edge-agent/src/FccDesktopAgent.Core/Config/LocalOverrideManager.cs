using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Security;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Config;

/// <summary>
/// Manages local FCC connection configuration overrides stored in <c>overrides.json</c>
/// in the agent data directory.
///
/// Overrides are applied on top of cloud-delivered FCC config, allowing on-site
/// technicians to adjust connection parameters (host, port) without waiting for
/// a cloud config push. When no overrides are set, cloud values are used as-is.
/// </summary>
public sealed class LocalOverrideManager
{
    private const string OverridesFileName = "overrides.json";
    private const string HmacFileName = "overrides.hmac";
    private const string HmacKeyName = "config:overrides_hmac_key";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly string _hmacFilePath;
    private readonly ICredentialStore? _credentialStore;
    private readonly ILogger<LocalOverrideManager> _logger;
    private readonly object _lock = new();
    private OverrideData? _cached;

    public LocalOverrideManager(ICredentialStore credentialStore, ILogger<LocalOverrideManager> logger)
    {
        var baseDir = AgentDataDirectory.Resolve();
        _filePath = Path.Combine(baseDir, OverridesFileName);
        _hmacFilePath = Path.Combine(baseDir, HmacFileName);
        _credentialStore = credentialStore;
        _logger = logger;
    }

    /// <summary>Backward-compatible constructor (no HMAC protection).</summary>
    public LocalOverrideManager(ILogger<LocalOverrideManager> logger)
        : this(credentialStore: null!, logger)
    {
    }

    /// <summary>Test constructor that allows overriding the data directory.</summary>
    internal LocalOverrideManager(ILogger<LocalOverrideManager> logger, string baseDirectory)
        : this(credentialStore: null!, logger)
    {
        _filePath = Path.Combine(baseDirectory, OverridesFileName);
        _hmacFilePath = Path.Combine(baseDirectory, HmacFileName);
    }

    /// <summary>Test constructor with credential store and custom base directory.</summary>
    internal LocalOverrideManager(ICredentialStore credentialStore, ILogger<LocalOverrideManager> logger, string baseDirectory)
    {
        _filePath = Path.Combine(baseDirectory, OverridesFileName);
        _hmacFilePath = Path.Combine(baseDirectory, HmacFileName);
        _credentialStore = credentialStore;
        _logger = logger;
    }

    // ── Effective value getters ──────────────────────────────────────────────

    /// <summary>Returns the overridden FCC host, or the cloud value if no override is set.</summary>
    public string GetEffectiveFccHost(string cloudHost)
    {
        var data = Load();
        return !string.IsNullOrWhiteSpace(data.FccHost) ? data.FccHost : cloudHost;
    }

    /// <summary>Returns the overridden FCC port, or the cloud value if no override is set.</summary>
    public int GetEffectiveFccPort(int cloudPort)
    {
        var data = Load();
        return data.FccPort is > 0 and <= 65535 ? data.FccPort.Value : cloudPort;
    }

    /// <summary>Returns the overridden JPL port, or the cloud value if no override is set.</summary>
    public int? GetEffectiveJplPort(int? cloudJplPort)
    {
        var data = Load();
        return data.JplPort is > 0 and <= 65535 ? data.JplPort : cloudJplPort;
    }

    /// <summary>Returns the overridden WebSocket port, or the cloud value if no override is set.</summary>
    public int? GetEffectiveWsPort(int? cloudWsPort)
    {
        var data = Load();
        return data.WsPort is > 0 and <= 65535 ? data.WsPort : cloudWsPort;
    }

    // ── Raw override values ─────────────────────────────────────────────────

    /// <summary>Returns the current overridden FCC host, or null if not set.</summary>
    public string? FccHost => Load().FccHost;

    /// <summary>Returns the current overridden FCC port, or null if not set.</summary>
    public int? FccPort => Load().FccPort;

    /// <summary>Returns the current overridden JPL port, or null if not set.</summary>
    public int? JplPort => Load().JplPort;

    /// <summary>Returns the current overridden WebSocket port, or null if not set.</summary>
    public int? WsPort => Load().WsPort;

    // ── Save / clear ────────────────────────────────────────────────────────

    /// <summary>
    /// Saves an individual override value. Validates host format and port ranges.
    /// </summary>
    public void SaveOverride(string key, string value)
    {
        var data = Load();
        switch (key)
        {
            case nameof(OverrideData.FccHost):
                if (!IsValidHostOrIp(value))
                    throw new ArgumentException($"Invalid host/IP: '{value}'. Must be a valid IPv4 address or hostname.");
                data.FccHost = value;
                break;
            case nameof(OverrideData.FccPort):
                data.FccPort = ParseAndValidatePort(value);
                break;
            case nameof(OverrideData.JplPort):
                data.JplPort = ParseAndValidatePort(value);
                break;
            case nameof(OverrideData.WsPort):
                data.WsPort = ParseAndValidatePort(value);
                break;
            default:
                throw new ArgumentException($"Unknown override key: '{key}'");
        }

        Persist(data);
        _logger.LogInformation("Override saved: {Key}", key);
    }

    /// <summary>Saves all override values at once. Validates before persisting.</summary>
    public void SaveAll(string? fccHost, int? fccPort, int? jplPort, int? wsPort)
    {
        if (!string.IsNullOrWhiteSpace(fccHost) && !IsValidHostOrIp(fccHost))
            throw new ArgumentException($"Invalid host/IP: '{fccHost}'");

        if (fccPort.HasValue && !IsValidPort(fccPort.Value))
            throw new ArgumentException($"Port out of range: {fccPort.Value}. Must be 1-65535.");

        if (jplPort.HasValue && !IsValidPort(jplPort.Value))
            throw new ArgumentException($"JPL port out of range: {jplPort.Value}. Must be 1-65535.");

        if (wsPort.HasValue && !IsValidPort(wsPort.Value))
            throw new ArgumentException($"WebSocket port out of range: {wsPort.Value}. Must be 1-65535.");

        var data = new OverrideData
        {
            FccHost = string.IsNullOrWhiteSpace(fccHost) ? null : fccHost.Trim(),
            FccPort = fccPort,
            JplPort = jplPort,
            WsPort = wsPort,
        };

        Persist(data);
        _logger.LogInformation("All overrides saved (host={Host}, port={Port}, jplPort={JplPort}, wsPort={WsPort})",
            data.FccHost ?? "(none)", data.FccPort?.ToString() ?? "(none)",
            data.JplPort?.ToString() ?? "(none)", data.WsPort?.ToString() ?? "(none)");
    }

    /// <summary>Clears all overrides, restoring cloud defaults.</summary>
    public void ClearAllOverrides()
    {
        lock (_lock) _cached = null;

        try
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
            if (File.Exists(_hmacFilePath))
                File.Delete(_hmacFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete overrides file");
        }

        _logger.LogInformation("All overrides cleared — reverted to cloud defaults");
    }

    /// <summary>Returns true if any override values are currently set.</summary>
    public bool HasOverrides()
    {
        var data = Load();
        return !string.IsNullOrWhiteSpace(data.FccHost)
            || data.FccPort.HasValue
            || data.JplPort.HasValue
            || data.WsPort.HasValue;
    }

    // ── Validation helpers ──────────────────────────────────────────────────

    public static bool IsValidHostOrIp(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253)
            return false;

        // Accept valid IPv4 addresses
        if (IPAddress.TryParse(value, out var addr) && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return true;

        // Accept valid hostnames (alphanumeric + hyphens, dot-separated labels)
        return System.Text.RegularExpressions.Regex.IsMatch(value,
            @"^([a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?\.)*[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?$");
    }

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    // ── Persistence ─────────────────────────────────────────────────────────

    private OverrideData Load()
    {
        lock (_lock)
        {
            if (_cached is not null)
                return _cached;
        }

        if (!File.Exists(_filePath))
        {
            var empty = new OverrideData();
            lock (_lock) _cached = empty;
            return empty;
        }

        try
        {
            var json = File.ReadAllText(_filePath);

            // S-DSK-018: Validate HMAC integrity before trusting the file contents
            if (!VerifyHmac(json))
            {
                _logger.LogWarning(
                    "Overrides file HMAC validation failed — file may have been tampered with. Ignoring overrides.");
                var fallback = new OverrideData();
                lock (_lock) _cached = fallback;
                return fallback;
            }

            var data = JsonSerializer.Deserialize<OverrideData>(json, JsonOptions) ?? new OverrideData();
            lock (_lock) _cached = data;
            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Failed to read overrides.json — treating as no overrides");
            var fallback = new OverrideData();
            lock (_lock) _cached = fallback;
            return fallback;
        }
    }

    private void Persist(OverrideData data)
    {
        lock (_lock) _cached = data;

        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(_filePath, json);

            // S-DSK-018: Write HMAC alongside the data file
            WriteHmac(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write overrides.json");
            throw;
        }
    }

    // ── HMAC integrity protection (S-DSK-018) ──────────────────────────────

    private byte[] GetOrCreateHmacKey()
    {
        if (_credentialStore is null)
            return Array.Empty<byte>();

        var keyBase64 = _credentialStore.GetSecretAsync(HmacKeyName).GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(keyBase64))
            return Convert.FromBase64String(keyBase64);

        // Generate and persist a new 256-bit HMAC key
        var newKey = RandomNumberGenerator.GetBytes(32);
        _credentialStore.SetSecretAsync(HmacKeyName, Convert.ToBase64String(newKey)).GetAwaiter().GetResult();
        return newKey;
    }

    private bool VerifyHmac(string json)
    {
        if (_credentialStore is null)
            return true; // No credential store — skip integrity check

        if (!File.Exists(_hmacFilePath))
        {
            // First load after upgrade — no HMAC file yet. Accept and write HMAC for next load.
            WriteHmac(json);
            return true;
        }

        try
        {
            var key = GetOrCreateHmacKey();
            if (key.Length == 0) return true;

            var storedHmac = File.ReadAllText(_hmacFilePath).Trim();
            var computedHmac = ComputeHmac(json, key);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(storedHmac),
                Convert.FromBase64String(computedHmac));
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            _logger.LogWarning(ex, "Failed to verify overrides HMAC");
            return false;
        }
    }

    private void WriteHmac(string json)
    {
        if (_credentialStore is null) return;

        try
        {
            var key = GetOrCreateHmacKey();
            if (key.Length == 0) return;

            var hmac = ComputeHmac(json, key);
            File.WriteAllText(_hmacFilePath, hmac);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write overrides HMAC file");
        }
    }

    private static string ComputeHmac(string data, byte[] key)
    {
        var dataBytes = Encoding.UTF8.GetBytes(data);
        var hash = HMACSHA256.HashData(key, dataBytes);
        return Convert.ToBase64String(hash);
    }

    private static int ParseAndValidatePort(string value)
    {
        if (!int.TryParse(value, out var port))
            throw new ArgumentException($"Port must be a number: '{value}'");
        if (!IsValidPort(port))
            throw new ArgumentException($"Port out of range: {port}. Must be 1-65535.");
        return port;
    }

    // ── Override data model ─────────────────────────────────────────────────

    internal sealed class OverrideData
    {
        public string? FccHost { get; set; }
        public int? FccPort { get; set; }
        public int? JplPort { get; set; }
        public int? WsPort { get; set; }
    }
}

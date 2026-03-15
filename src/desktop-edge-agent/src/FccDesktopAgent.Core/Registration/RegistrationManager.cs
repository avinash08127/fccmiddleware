using System.Text.Json;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.MasterData;
using FccDesktopAgent.Core.Security;
using FccDesktopAgent.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Registration;

/// <summary>
/// Reads/writes <c>registration.json</c> from the agent data directory.
/// Also implements <see cref="IPostConfigureOptions{T}"/> to overlay identity fields
/// (DeviceId, SiteId, CloudBaseUrl) from registration state into <see cref="AgentConfiguration"/>
/// so that all workers see the correct identity without manual wiring.
/// </summary>
public sealed class RegistrationManager : IRegistrationManager, IPostConfigureOptions<AgentConfiguration>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<RegistrationManager> _logger;
    private readonly ICredentialStore? _credentialStore;
    private readonly SiteDataManager? _siteDataManager;
    private readonly IAgentCommandStateStore? _commandStateStore;
    private readonly string? _baseDirectoryOverride;
    private readonly object _lock = new();
    private RegistrationState? _cached;
    // P-DSK-010: Version counter incremented on SaveStateAsync. PostConfigure only
    // re-reads from disk when _lastSeenVersion != _cacheVersion (i.e. state changed).
    private volatile int _cacheVersion;
    private int _lastSeenVersion = -1; // force first load

    public RegistrationManager(
        ILogger<RegistrationManager> logger,
        SiteDataManager siteDataManager,
        ICredentialStore credentialStore,
        IAgentCommandStateStore commandStateStore)
    {
        _logger = logger;
        _siteDataManager = siteDataManager;
        _credentialStore = credentialStore;
        _commandStateStore = commandStateStore;
    }

    /// <summary>Test constructor that overrides the data directory.</summary>
    internal RegistrationManager(
        ILogger<RegistrationManager> logger,
        string baseDirectory,
        ICredentialStore? credentialStore = null,
        IAgentCommandStateStore? commandStateStore = null)
    {
        _logger = logger;
        _baseDirectoryOverride = baseDirectory;
        _credentialStore = credentialStore;
        _commandStateStore = commandStateStore;
        // L-04: _siteDataManager intentionally null in test constructor
    }

    // ── IRegistrationManager ─────────────────────────────────────────────────

    public event EventHandler? DeviceDecommissioned;
    public event EventHandler? ReprovisioningRequired;

    /// <inheritdoc />
    public bool IsDecommissioned
    {
        get
        {
            lock (_lock)
            {
                return _cached?.IsDecommissioned ?? false;
            }
        }
    }

    public bool IsRegistered
    {
        get
        {
            lock (_lock)
            {
                return _cached?.IsRegistered ?? false;
            }
        }
    }

    public RegistrationState LoadState()
    {
        // M-04: Return a defensive copy so concurrent callers (MarkDecommissionedAsync,
        // MarkReprovisioningRequiredAsync) don't mutate the same shared object.
        lock (_lock)
        {
            if (_cached is not null)
                return _cached.Clone();
        }

        var path = GetFilePath();
        if (!File.Exists(path))
        {
            _logger.LogDebug("No registration.json found — returning default unregistered state");
            var defaultState = new RegistrationState();
            lock (_lock) _cached = defaultState;
            return defaultState;
        }

        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<RegistrationState>(json, JsonOptions)
                        ?? new RegistrationState();
            lock (_lock) _cached = state;

            if (state.IsDecommissioned)
                _logger.LogWarning("Device is decommissioned (deviceId={DeviceId})", state.DeviceId);
            else if (state.IsRegistered)
                _logger.LogInformation("Device is registered (deviceId={DeviceId}, site={SiteCode})",
                    state.DeviceId, state.SiteCode);

            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Failed to read registration.json — treating as unregistered");
            var fallback = new RegistrationState();
            lock (_lock) _cached = fallback;
            return fallback;
        }
    }

    /// <summary>
    /// H-06: Atomic write — writes to a temp file first, then replaces the target.
    /// If the process crashes or power is lost mid-write, the temp file is corrupted
    /// instead of the real registration.json, preserving the previous valid state.
    /// </summary>
    public async Task SaveStateAsync(RegistrationState state, CancellationToken ct = default)
    {
        var path = GetFilePath();
        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(state, JsonOptions);

        await File.WriteAllTextAsync(tmpPath, json, ct);

        if (File.Exists(path))
            File.Replace(tmpPath, path, destinationBackupFileName: null);
        else
            File.Move(tmpPath, path);

        lock (_lock) _cached = state;
        // P-DSK-010: Bump version so PostConfigure knows to re-read
        Interlocked.Increment(ref _cacheVersion);

        _logger.LogInformation("Registration state saved (registered={IsRegistered}, deviceId={DeviceId})",
            state.IsRegistered, state.DeviceId);
    }

    public async Task MarkDecommissionedAsync(CancellationToken ct = default)
    {
        var state = LoadState();
        state.IsDecommissioned = true;

        // OB-S01: Purge device and refresh tokens from credential store on decommission.
        // Server-side revocation makes tokens unusable, but plaintext/encrypted values
        // should not persist in the platform credential store.
        await PurgeTokensFromCredentialStoreAsync(ct);

        await SaveStateAsync(state, ct);
        if (_commandStateStore is not null)
        {
            await _commandStateStore.SetNoticeAsync(
                OperatorNoticeKind.Decommissioned,
                "This device has been decommissioned by the cloud administrator.",
                ct);
        }
        _logger.LogWarning("Device marked as decommissioned");
        DeviceDecommissioned?.Invoke(this, EventArgs.Empty);
    }

    public async Task MarkReprovisioningRequiredAsync(CancellationToken ct = default)
    {
        var state = LoadState();
        state.IsRegistered = false;
        state.IsDecommissioned = false;

        // OB-S01: Purge device and refresh tokens before re-provisioning.
        // New tokens will be issued during the next provisioning flow.
        await PurgeTokensFromCredentialStoreAsync(ct);

        await SaveStateAsync(state, ct);
        if (_commandStateStore is not null)
        {
            await _commandStateStore.SetNoticeAsync(
                OperatorNoticeKind.ReprovisioningRequired,
                "Re-provisioning required. Complete setup again before the agent can resume normal operation.",
                ct);
        }
        _logger.LogWarning("Device marked for re-provisioning — restart to begin provisioning wizard");
        // M-10: Notify UI so it can show a re-provisioning prompt instead of
        // silently halting uploads with no user-visible indication.
        ReprovisioningRequired?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Syncs site equipment data from the cloud config to a local JSON file.
    /// Called after successful registration when the site config is available.
    /// </summary>
    public async Task SyncSiteDataAsync(SiteConfig config)
    {
        // L-04: Log when SiteDataManager is unavailable so silent no-ops are visible in diagnostics
        if (_siteDataManager is null)
        {
            _logger.LogWarning("SiteDataManager is not available — equipment metadata sync skipped");
            return;
        }

        await _siteDataManager.SyncFromConfigAsync(config);
    }

    // ── IPostConfigureOptions<AgentConfiguration> ────────────────────────────

    public void PostConfigure(string? name, AgentConfiguration options)
    {
        // P-DSK-010: Only invalidate the cache when the registration state has actually
        // changed (after SaveStateAsync bumps the version counter). This avoids a
        // synchronous File.ReadAllText on every IOptions/IOptionsMonitor resolution.
        var currentVersion = _cacheVersion;
        if (_lastSeenVersion != currentVersion)
        {
            lock (_lock) _cached = null;
            _lastSeenVersion = currentVersion;
        }

        var state = LoadState();
        if (!state.IsRegistered) return;

        if (!string.IsNullOrEmpty(state.DeviceId))
            options.DeviceId = state.DeviceId;

        if (!string.IsNullOrEmpty(state.SiteCode))
            options.SiteId = state.SiteCode;

        // Resolve cloud base URL from environment map when set, otherwise use explicit URL.
        if (!string.IsNullOrEmpty(state.Environment))
        {
            options.Environment = state.Environment;
            var resolved = CloudEnvironments.Resolve(state.Environment);
            if (resolved is not null)
                options.CloudBaseUrl = resolved;
            else if (!string.IsNullOrEmpty(state.CloudBaseUrl))
                options.CloudBaseUrl = state.CloudBaseUrl;
        }
        else if (!string.IsNullOrEmpty(state.CloudBaseUrl))
        {
            options.CloudBaseUrl = state.CloudBaseUrl;
        }

        if (!string.IsNullOrEmpty(state.LegalEntityId))
            options.LegalEntityId = state.LegalEntityId;
    }

    // ── Private ──────────────────────────────────────────────────────────────

    /// <summary>
    /// OB-S01: Purges device JWT and refresh token from the platform credential store.
    /// Best-effort — failures are logged but do not block decommission/re-provisioning.
    /// </summary>
    private async Task PurgeTokensFromCredentialStoreAsync(CancellationToken ct)
    {
        if (_credentialStore is null)
        {
            _logger.LogDebug("No credential store available — skipping token purge");
            return;
        }

        try
        {
            await _credentialStore.DeleteSecretAsync(CredentialKeys.DeviceTokenBundle, ct);
            await _credentialStore.DeleteSecretAsync(CredentialKeys.DeviceTokenBundleStaging, ct);
            await _credentialStore.DeleteSecretAsync(CredentialKeys.DeviceTokenRefreshPending, ct);
            await _credentialStore.DeleteSecretAsync(CredentialKeys.DeviceToken, ct);
            await _credentialStore.DeleteSecretAsync(CredentialKeys.RefreshToken, ct);
            _logger.LogInformation("Device and refresh tokens purged from credential store");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to purge tokens from credential store — tokens may remain in platform store");
        }
    }

    private string GetFilePath() =>
        Path.Combine(_baseDirectoryOverride ?? AgentDataDirectory.Resolve(), "registration.json");
}

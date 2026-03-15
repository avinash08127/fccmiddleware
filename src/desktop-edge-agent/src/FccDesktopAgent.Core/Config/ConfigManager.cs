using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.MasterData;
using FccDesktopAgent.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace FccDesktopAgent.Core.Config;

/// <summary>
/// Manages the agent's cloud-sourced configuration.
///
/// Integrates with the .NET Options infrastructure:
/// <list type="bullet">
///   <item><see cref="IOptionsChangeTokenSource{T}"/> — signals <see cref="IOptionsMonitor{T}"/>
///     when cloud config changes, triggering re-evaluation.</item>
///   <item><see cref="IPostConfigureOptions{T}"/> — overlays hot-reloadable cloud values onto
///     <see cref="AgentConfiguration"/> after the base config binding runs.</item>
/// </list>
///
/// On startup, loads last-known-good config from SQLite. On each successful cloud poll,
/// validates the new config, stores it, and applies hot-reloadable fields. Restart-required
/// changes are flagged but never forced.
/// </summary>
public sealed class ConfigManager
    : IConfigManager,
      IOptionsChangeTokenSource<AgentConfiguration>,
      IPostConfigureOptions<AgentConfiguration>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConfigManager> _logger;
    private readonly IAuditLogger? _auditLogger;
    private readonly object _lock = new();

    private SiteConfig? _current;
    private string? _currentVersion;
    private bool _restartRequired;
    private long _currentPeerDirectoryVersion;
    private CancellationTokenSource _changeTokenSource = new();

    public ConfigManager(
        IServiceScopeFactory scopeFactory,
        ILogger<ConfigManager> logger,
        IAuditLogger? auditLogger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _auditLogger = auditLogger;
    }

    // ── IConfigManager ────────────────────────────────────────────────────────

    public SiteConfig? CurrentSiteConfig
    {
        get { lock (_lock) return _current; }
    }

    public string? CurrentConfigVersion
    {
        get { lock (_lock) return _currentVersion; }
    }

    public bool RestartRequired
    {
        get { lock (_lock) return _restartRequired; }
    }

    public long CurrentPeerDirectoryVersion => Interlocked.Read(ref _currentPeerDirectoryVersion);

    public bool IsPeerDirectoryStale(long cloudVersion) =>
        cloudVersion > Interlocked.Read(ref _currentPeerDirectoryVersion);

    public void UpdatePeerDirectoryVersion(long version) =>
        Interlocked.Exchange(ref _currentPeerDirectoryVersion, version);

    public Action? OnPeerDirectoryStale { get; set; }

    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

    public async Task<ConfigApplyResult> ApplyConfigAsync(
        SiteConfig newConfig, string rawJson, string configVersion, CancellationToken ct)
    {
        // Validate config version is strictly greater than current
        SiteConfig? previous;
        lock (_lock)
        {
            previous = _current;
            if (previous is not null && newConfig.ConfigVersion < previous.ConfigVersion)
            {
                _logger.LogDebug(
                    "Config version {New} is not greater than current {Current}; ignoring",
                    newConfig.ConfigVersion, previous.ConfigVersion);
                return new ConfigApplyResult(ConfigApplyOutcome.StaleVersion, newConfig.ConfigVersion);
            }
        }

        // Check effectiveAtUtc — defer if not yet effective
        if (newConfig.EffectiveAtUtc > DateTimeOffset.UtcNow)
        {
            _logger.LogInformation(
                "Config version {Version} not yet effective (effectiveAt={EffectiveAt}); deferring",
                newConfig.ConfigVersion, newConfig.EffectiveAtUtc);
            return new ConfigApplyResult(ConfigApplyOutcome.NotYetEffective, newConfig.ConfigVersion);
        }

        if (!DesktopFccRuntimeConfiguration.TryValidateSiteConfig(newConfig, out var validationError))
        {
            _logger.LogWarning(
                "Config version {Version} rejected: {ValidationError}",
                newConfig.ConfigVersion,
                validationError);
            return new ConfigApplyResult(ConfigApplyOutcome.Rejected, newConfig.ConfigVersion, ErrorMessage: validationError);
        }

        // Detect changed sections
        var hotReloaded = new List<string>();
        var restartSections = new List<string>();
        var restartRequiredSet = new HashSet<string>(
            newConfig.Rollout?.RequiresRestartSections ?? [], StringComparer.OrdinalIgnoreCase);

        DetectChanges(previous, newConfig, restartRequiredSet, hotReloaded, restartSections);

        // Store in database
        await StoreConfigAsync(rawJson, configVersion, ct);

        if (!string.IsNullOrWhiteSpace(newConfig.SiteHa.PeerSharedSecret))
        {
            try
            {
                await using var secretScope = _scopeFactory.CreateAsyncScope();
                var credentialStore = secretScope.ServiceProvider.GetService<ICredentialStore>();
                if (credentialStore is not null)
                {
                    await credentialStore.SetSecretAsync(
                        CredentialKeys.PeerSharedSecret,
                        newConfig.SiteHa.PeerSharedSecret,
                        ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist peer shared secret from cloud config");
            }
        }

        // F-DSK-029/030: Sync nozzle mappings and site data to DB on every config update.
        try
        {
            await using var dataScope = _scopeFactory.CreateAsyncScope();
            var siteDataManager = dataScope.ServiceProvider.GetService<SiteDataManager>();
            if (siteDataManager is not null)
            {
                await siteDataManager.SyncFromConfigAsync(newConfig);
                await siteDataManager.SyncNozzleMappingsToDbAsync(newConfig, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync site data/nozzle mappings from config (non-fatal)");
        }

        // Apply in memory
        lock (_lock)
        {
            _current = newConfig;
            _currentVersion = configVersion;
            if (restartSections.Count > 0)
                _restartRequired = true;
        }

        // Signal IOptionsMonitor change so hot-reloadable fields take effect
        if (hotReloaded.Count > 0 || previous is null)
            SignalOptionsChange();

        // Log restart-required changes
        if (restartSections.Count > 0)
        {
            _logger.LogWarning(
                "Config version {Version}: sections [{Sections}] require agent restart to take effect",
                newConfig.ConfigVersion, string.Join(", ", restartSections));
        }

        if (hotReloaded.Count > 0)
        {
            _logger.LogInformation(
                "Config version {Version}: hot-reloaded sections [{Sections}]",
                newConfig.ConfigVersion, string.Join(", ", hotReloaded));
        }

        // Raise event
        ConfigChanged?.Invoke(this, new ConfigChangedEventArgs
        {
            ConfigVersion = newConfig.ConfigVersion,
            HotReloadedSections = hotReloaded,
            RestartRequiredSections = restartSections,
        });

        // F-DSK-046: Audit log entry for config changes
        _ = _auditLogger?.LogEventAsync("CONFIG_APPLIED",
            $"Version {newConfig.ConfigVersion} applied (hot-reloaded: [{string.Join(", ", hotReloaded)}])",
            "SiteConfig", newConfig.ConfigVersion.ToString(), ct: CancellationToken.None);

        return new ConfigApplyResult(
            ConfigApplyOutcome.Applied,
            newConfig.ConfigVersion,
            hotReloaded,
            restartSections);
    }

    public async Task LoadFromDatabaseAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var record = await db.AgentConfigs.FindAsync([1], ct);
        if (record?.ConfigJson is null or "{}")
        {
            _logger.LogDebug("No stored config found in database");
            return;
        }

        try
        {
            var config = JsonSerializer.Deserialize<SiteConfig>(record.ConfigJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (config is not null)
            {
                // Restore peer directory version from SyncState
                var syncState = await db.SyncStates.FindAsync([1], ct);
                if (syncState is not null)
                    Interlocked.Exchange(ref _currentPeerDirectoryVersion, syncState.PeerDirectoryVersion);

                lock (_lock)
                {
                    _current = config;
                    _currentVersion = record.ConfigVersion;
                }
                SignalOptionsChange();
                _logger.LogInformation(
                    "Loaded config version {Version} from database (applied at {AppliedAt}, peerDirVersion={PeerDirVersion})",
                    record.ConfigVersion, record.AppliedAt, Interlocked.Read(ref _currentPeerDirectoryVersion));
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize stored config; starting fresh");
        }
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var record = await db.AgentConfigs.FindAsync([1], ct);
        if (record is not null)
            db.AgentConfigs.Remove(record);

        var syncState = await db.SyncStates.FindAsync([1], ct);
        if (syncState is not null)
        {
            syncState.ConfigVersion = null;
            syncState.LastConfigSyncAt = null;
            syncState.PeerDirectoryVersion = 0;
            syncState.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        Interlocked.Exchange(ref _currentPeerDirectoryVersion, 0);

        lock (_lock)
        {
            _current = null;
            _currentVersion = null;
            _restartRequired = false;
        }

        SignalOptionsChange();
    }

    // ── IOptionsChangeTokenSource<AgentConfiguration> ─────────────────────────

    public string? Name => Options.DefaultName;

    public IChangeToken GetChangeToken()
    {
        lock (_lock)
            return new CancellationChangeToken(_changeTokenSource.Token);
    }

    // ── IPostConfigureOptions<AgentConfiguration> ─────────────────────────────

    public void PostConfigure(string? name, AgentConfiguration options)
    {
        SiteConfig? config;
        lock (_lock) config = _current;
        if (config is null) return;

        ApplyHotReloadFields(options, config);
    }

    // ── Hot-reload field mapping ──────────────────────────────────────────────

    internal static void ApplyHotReloadFields(AgentConfiguration target, SiteConfig source)
    {
        // ── Identity fields (from cloud config, not just registration) ──
        if (!string.IsNullOrWhiteSpace(source.Identity?.DeviceId))
            target.DeviceId = source.Identity.DeviceId;

        if (!string.IsNullOrWhiteSpace(source.Identity?.SiteCode))
            target.SiteId = source.Identity.SiteCode;

        if (source.Identity is { LegalEntityId: { } legalEntityId } && legalEntityId != Guid.Empty)
            target.LegalEntityId = legalEntityId.ToString();

        // ── FCC runtime fields ──
        if (DesktopFccRuntimeConfiguration.TryParseVendor(source.Fcc?.Vendor, out var vendor))
            target.FccVendor = vendor;

        if (!string.IsNullOrWhiteSpace(source.Fcc?.HostAddress) && source.Fcc.Port is > 0)
            target.FccBaseUrl = $"http://{source.Fcc.HostAddress}:{source.Fcc.Port.Value}";

        // FCC polling
        if (source.Fcc?.PullIntervalSeconds is > 0)
            target.FccPollIntervalSeconds = source.Fcc.PullIntervalSeconds.Value;

        if (source.Fcc?.IngestionMode is not null)
            target.IngestionMode = ParseIngestionMode(source.Fcc.IngestionMode);

        if (source.Fcc?.HeartbeatIntervalSeconds > 0)
            target.ConnectivityProbeIntervalSeconds = source.Fcc.HeartbeatIntervalSeconds;

        // ── Sync intervals ──
        if (source.Sync is not null)
        {
            if (source.Sync.UploadIntervalSeconds > 0)
                target.CloudSyncIntervalSeconds = source.Sync.UploadIntervalSeconds;
            if (source.Sync.UploadBatchSize > 0)
                target.UploadBatchSize = source.Sync.UploadBatchSize;
            if (source.Sync.SyncedStatusPollIntervalSeconds > 0)
                target.StatusPollIntervalSeconds = source.Sync.SyncedStatusPollIntervalSeconds;
            if (source.Sync.ConfigPollIntervalSeconds > 0)
                target.ConfigPollIntervalSeconds = source.Sync.ConfigPollIntervalSeconds;
            if (!string.IsNullOrWhiteSpace(source.Sync.Environment))
                target.Environment = source.Sync.Environment;
            // S-DSK-019: Validate HTTPS before accepting a cloud-pushed CloudBaseUrl
            if (!string.IsNullOrWhiteSpace(source.Sync.CloudBaseUrl))
            {
                if (CloudUrlGuard.IsSecure(source.Sync.CloudBaseUrl))
                    target.CloudBaseUrl = source.Sync.CloudBaseUrl;
                // else: silently reject — non-HTTPS URLs must not override the current value
            }
        }

        // ── Buffer ──
        if (source.Buffer is not null)
        {
            if (source.Buffer.RetentionDays > 0)
                target.RetentionDays = source.Buffer.RetentionDays;
            if (source.Buffer.CleanupIntervalHours > 0)
                target.CleanupIntervalHours = source.Buffer.CleanupIntervalHours;
        }

        // ── Local API ──
        if (source.LocalApi is not null)
        {
            if (source.LocalApi.LocalhostPort > 0)
                target.LocalApiPort = source.LocalApi.LocalhostPort;
        }

        if (source.SiteHa is not null)
        {
            target.SiteHaEnabled = source.SiteHa.Enabled;
            target.AutoFailoverEnabled = source.SiteHa.AutoFailoverEnabled;
            target.SiteHaPriority = source.SiteHa.Priority;
            target.RoleCapability = source.SiteHa.RoleCapability;
            target.CurrentRole = source.SiteHa.CurrentRole;
            target.HeartbeatIntervalSeconds = source.SiteHa.HeartbeatIntervalSeconds;
            target.FailoverTimeoutSeconds = source.SiteHa.FailoverTimeoutSeconds;
            target.MaxReplicationLagSeconds = source.SiteHa.MaxReplicationLagSeconds;
            target.ReplicationEnabled = source.SiteHa.ReplicationEnabled;
            target.ProxyingEnabled = source.SiteHa.ProxyingEnabled;
            target.LeaderEpoch = source.SiteHa.LeaderEpoch;
            target.PeerApiPort = source.SiteHa.PeerApiPort;
        }

        // ── Telemetry ──
        if (source.Telemetry is not null)
        {
            if (source.Telemetry.TelemetryIntervalSeconds > 0)
                target.TelemetryIntervalSeconds = source.Telemetry.TelemetryIntervalSeconds;
        }

        // T-DSK-015: Load additional certificate pins from cloud config so emergency
        // pin rotations can be pushed without a software update.
        if (source.Sync?.CertificatePins is { Length: > 0 })
        {
            target.AdditionalCertificatePins = [.. source.Sync.CertificatePins];
            CertificatePinValidator.LoadAdditionalPins(source.Sync.CertificatePins);
        }
    }

    // ── Section change detection ──────────────────────────────────────────────

    private static void DetectChanges(
        SiteConfig? previous, SiteConfig newConfig,
        HashSet<string> restartRequiredSet,
        List<string> hotReloaded, List<string> restartSections)
    {
        if (previous is null)
        {
            // First config ever — treat all as hot-reloaded (initial apply)
            hotReloaded.Add("initial");
            return;
        }

        CheckSection("identity", previous.Identity, newConfig.Identity, restartRequiredSet, hotReloaded, restartSections);
        CheckSection("site", previous.Site, newConfig.Site, restartRequiredSet, hotReloaded, restartSections);
        CheckSection("fcc", previous.Fcc, newConfig.Fcc, restartRequiredSet, hotReloaded, restartSections);
        CheckSection("sync", previous.Sync, newConfig.Sync, restartRequiredSet, hotReloaded, restartSections);
        CheckSection("buffer", previous.Buffer, newConfig.Buffer, restartRequiredSet, hotReloaded, restartSections);
        CheckSection("localApi", previous.LocalApi, newConfig.LocalApi, restartRequiredSet, hotReloaded, restartSections);
        CheckSection("telemetry", previous.Telemetry, newConfig.Telemetry, restartRequiredSet, hotReloaded, restartSections);
        CheckSection("fiscalization", previous.Fiscalization, newConfig.Fiscalization, restartRequiredSet, hotReloaded, restartSections);
        CheckSection("mappings", previous.Mappings, newConfig.Mappings, restartRequiredSet, hotReloaded, restartSections);
        CheckSection("siteHa", previous.SiteHa, newConfig.SiteHa, restartRequiredSet, hotReloaded, restartSections);
        CheckSection("rollout", previous.Rollout, newConfig.Rollout, restartRequiredSet, hotReloaded, restartSections);
    }

    private static void CheckSection(
        string sectionName, object? previous, object? current,
        HashSet<string> restartRequiredSet,
        List<string> hotReloaded, List<string> restartSections)
    {
        var prevJson = JsonSerializer.Serialize(previous);
        var currJson = JsonSerializer.Serialize(current);

        if (string.Equals(prevJson, currJson, StringComparison.Ordinal))
            return;

        if (restartRequiredSet.Contains(sectionName))
            restartSections.Add(sectionName);
        else
            hotReloaded.Add(sectionName);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static IngestionMode ParseIngestionMode(string mode) => mode.ToUpperInvariant() switch
    {
        "RELAY" => IngestionMode.Relay,
        "BUFFER_ALWAYS" => IngestionMode.BufferAlways,
        "CLOUD_DIRECT" => IngestionMode.CloudDirect,
        _ => IngestionMode.Relay,
    };

    private void SignalOptionsChange()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old = _changeTokenSource;
            _changeTokenSource = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }

    private async Task StoreConfigAsync(string rawJson, string configVersion, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var now = DateTimeOffset.UtcNow;

        var record = await db.AgentConfigs.FindAsync([1], ct);
        if (record is null)
        {
            record = new AgentConfigRecord
            {
                Id = 1,
                ConfigJson = rawJson,
                ConfigVersion = configVersion,
                AppliedAt = now,
                UpdatedAt = now,
            };
            db.AgentConfigs.Add(record);
        }
        else
        {
            record.ConfigJson = rawJson;
            record.ConfigVersion = configVersion;
            record.AppliedAt = now;
            record.UpdatedAt = now;
        }

        // Also update SyncState to track config version
        var syncState = await db.SyncStates.FindAsync([1], ct);
        if (syncState is null)
        {
            syncState = new SyncStateRecord
            {
                Id = 1,
                ConfigVersion = configVersion,
                LastConfigSyncAt = now,
                PeerDirectoryVersion = Interlocked.Read(ref _currentPeerDirectoryVersion),
                UpdatedAt = now,
            };
            db.SyncStates.Add(syncState);
        }
        else
        {
            syncState.ConfigVersion = configVersion;
            syncState.LastConfigSyncAt = now;
            syncState.PeerDirectoryVersion = Interlocked.Read(ref _currentPeerDirectoryVersion);
            syncState.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}

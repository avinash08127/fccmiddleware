using System.Text.Json;
using FccDesktopAgent.Core.Security;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Config;

/// <summary>
/// T-DSK-016: Encapsulates the SiteConfig construction, validation, and apply
/// orchestration that was previously inlined in ConfigurationPage code-behind.
/// This service is unit-testable and reusable by both the GUI and headless host.
/// </summary>
public sealed class ConfigSaveService
{
    private readonly IConfigManager _configManager;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<ConfigSaveService> _logger;

    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        WriteIndented = true,
    };

    public ConfigSaveService(
        IConfigManager configManager,
        ICredentialStore credentialStore,
        ILogger<ConfigSaveService> logger)
    {
        _configManager = configManager;
        _credentialStore = credentialStore;
        _logger = logger;
    }

    /// <summary>
    /// Builds a new <see cref="SiteConfig"/> from the provided field values,
    /// preserving all sections/fields not covered by <paramref name="fields"/>,
    /// and applies it through <see cref="IConfigManager"/>.
    /// </summary>
    public async Task<ConfigSaveResult> SaveAsync(ConfigSaveFields fields, CancellationToken ct)
    {
        var currentSite = _configManager.CurrentSiteConfig;

        // Clone existing sections to preserve ALL vendor-specific fields (F-DSK-018).
        var fcc = CloneSection(currentSite?.Fcc) ?? new SiteConfigFcc();
        fcc.PullIntervalSeconds = fields.FccPollIntervalSeconds;
        fcc.HeartbeatIntervalSeconds = fields.ConnectivityProbeIntervalSeconds;

        var sync = CloneSection(currentSite?.Sync) ?? new SiteConfigSync();
        sync.UploadBatchSize = fields.UploadBatchSize;
        sync.UploadIntervalSeconds = fields.CloudSyncIntervalSeconds;
        sync.ConfigPollIntervalSeconds = fields.ConfigPollIntervalSeconds;

        var buffer = CloneSection(currentSite?.Buffer) ?? new SiteConfigBuffer();
        buffer.RetentionDays = fields.RetentionDays;
        buffer.CleanupIntervalHours = fields.CleanupIntervalHours;

        var localApi = CloneSection(currentSite?.LocalApi) ?? new SiteConfigLocalApi();
        localApi.LocalhostPort = fields.LocalApiPort;

        var telemetry = CloneSection(currentSite?.Telemetry) ?? new SiteConfigTelemetry();
        telemetry.TelemetryIntervalSeconds = fields.TelemetryIntervalSeconds;
        telemetry.LogLevel = fields.LogLevel;

        var updated = new SiteConfig
        {
            SchemaVersion = currentSite?.SchemaVersion ?? "1.0",
            ConfigVersion = (currentSite?.ConfigVersion ?? 0) + 1,
            ConfigId = currentSite?.ConfigId ?? Guid.NewGuid().ToString(),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            EffectiveAtUtc = DateTimeOffset.UtcNow,
            Identity = currentSite?.Identity,
            Site = currentSite?.Site,
            Fcc = fcc,
            Sync = sync,
            Buffer = buffer,
            LocalApi = localApi,
            Telemetry = telemetry,
            Fiscalization = currentSite?.Fiscalization,
            Mappings = currentSite?.Mappings,
            Rollout = currentSite?.Rollout,
        };

        var rawJson = JsonSerializer.Serialize(updated, CloneOptions);

        var result = await _configManager.ApplyConfigAsync(
            updated, rawJson, updated.ConfigVersion.ToString(), ct);

        // Persist LAN API key if provided and changed
        if (!string.IsNullOrEmpty(fields.NewLanApiKey)
            && fields.NewLanApiKey != fields.PreviousLanApiKey)
        {
            await _credentialStore.SetSecretAsync(
                CredentialKeys.LanApiKey, fields.NewLanApiKey, ct);
        }

        return new ConfigSaveResult(result);
    }

    #pragma warning disable IL2026
    private static T? CloneSection<T>(T? source) where T : class
    {
        if (source is null) return null;
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source));
    }
    #pragma warning restore IL2026
}

/// <summary>
/// Input fields for <see cref="ConfigSaveService.SaveAsync"/>.
/// Maps 1:1 to editable UI controls — the service owns the SiteConfig construction.
/// </summary>
public sealed record ConfigSaveFields
{
    public int FccPollIntervalSeconds { get; init; } = 30;
    public int ConnectivityProbeIntervalSeconds { get; init; } = 30;
    public int UploadBatchSize { get; init; } = 50;
    public int CloudSyncIntervalSeconds { get; init; } = 60;
    public int ConfigPollIntervalSeconds { get; init; } = 60;
    public int RetentionDays { get; init; } = 7;
    public int CleanupIntervalHours { get; init; } = 24;
    public int LocalApiPort { get; init; } = 8585;
    public int TelemetryIntervalSeconds { get; init; } = 300;
    public string LogLevel { get; init; } = "Information";

    /// <summary>New LAN API key value from the UI (null = not changed).</summary>
    public string? NewLanApiKey { get; init; }

    /// <summary>Previously loaded LAN API key for change detection.</summary>
    public string? PreviousLanApiKey { get; init; }
}

/// <summary>
/// Wraps <see cref="ConfigApplyResult"/> for the UI layer.
/// </summary>
public sealed class ConfigSaveResult
{
    public ConfigSaveResult(ConfigApplyResult applyResult) => ApplyResult = applyResult;
    public ConfigApplyResult ApplyResult { get; }
}

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
    private readonly IApiKeyRefresher? _apiKeyRefresher;
    private readonly ILogger<ConfigSaveService> _logger;

    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        WriteIndented = true,
    };

    public ConfigSaveService(
        IConfigManager configManager,
        ICredentialStore credentialStore,
        ILogger<ConfigSaveService> logger,
        IApiKeyRefresher? apiKeyRefresher = null)
    {
        _configManager = configManager;
        _credentialStore = credentialStore;
        _logger = logger;
        _apiKeyRefresher = apiKeyRefresher;
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
        var fcc = (CloneSection(currentSite?.Fcc) ?? CreateDefaultFcc()) with
        {
            PullIntervalSeconds = fields.FccPollIntervalSeconds,
            HeartbeatIntervalSeconds = fields.ConnectivityProbeIntervalSeconds
        };

        var sync = (CloneSection(currentSite?.Sync) ?? CreateDefaultSync()) with
        {
            UploadBatchSize = fields.UploadBatchSize,
            UploadIntervalSeconds = fields.CloudSyncIntervalSeconds,
            ConfigPollIntervalSeconds = fields.ConfigPollIntervalSeconds
        };

        var buffer = (CloneSection(currentSite?.Buffer) ?? CreateDefaultBuffer()) with
        {
            RetentionDays = fields.RetentionDays,
            CleanupIntervalHours = fields.CleanupIntervalHours
        };

        var localApi = (CloneSection(currentSite?.LocalApi) ?? CreateDefaultLocalApi()) with
        {
            LocalhostPort = fields.LocalApiPort
        };

        var telemetry = (CloneSection(currentSite?.Telemetry) ?? CreateDefaultTelemetry()) with
        {
            TelemetryIntervalSeconds = fields.TelemetryIntervalSeconds,
            LogLevel = fields.LogLevel
        };

        var updated = new SiteConfig
        {
            SchemaVersion = currentSite?.SchemaVersion ?? "1.0",
            ConfigVersion = currentSite?.ConfigVersion ?? 0,
            ConfigId = currentSite?.ConfigId ?? Guid.NewGuid(),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            EffectiveAtUtc = DateTimeOffset.UtcNow,
            SourceRevision = currentSite?.SourceRevision ?? new SiteConfigSourceRevision(),
            Identity = currentSite?.Identity ?? CreatePlaceholderIdentity(),
            Site = currentSite?.Site ?? CreatePlaceholderSite(),
            Fcc = fcc,
            Sync = sync,
            Buffer = buffer,
            LocalApi = localApi,
            SiteHa = currentSite?.SiteHa ?? CreateDefaultSiteHa(),
            Telemetry = telemetry,
            Fiscalization = currentSite?.Fiscalization ?? CreateDefaultFiscalization(),
            Mappings = currentSite?.Mappings ?? CreateDefaultMappings(),
            Rollout = currentSite?.Rollout ?? CreateDefaultRollout(),
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

            // S-DSK-033: Refresh the cached API key in the running API stack so
            // the old key is revoked immediately without requiring an agent restart.
            if (_apiKeyRefresher is not null)
            {
                await _apiKeyRefresher.RefreshKeyAsync(ct);
                _logger.LogInformation("LAN API key rotated and live-refreshed in the running API stack");
            }
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

    private static SiteConfigFcc CreateDefaultFcc() => new()
    {
        Enabled = false,
        SecretEnvelope = new SiteConfigSecretEnvelope { Format = "NONE" },
        HeartbeatIntervalSeconds = 30,
        HeartbeatTimeoutSeconds = 60,
        PushSourceIpAllowList = []
    };

    private static SiteConfigSync CreateDefaultSync() => new()
    {
        CloudBaseUrl = "https://api.fccmiddleware.com",
        UploadBatchSize = 50,
        UploadIntervalSeconds = 60,
        SyncedStatusPollIntervalSeconds = 300,
        ConfigPollIntervalSeconds = 60,
        CursorStrategy = "FCC_TRANSACTION_ID",
        MaxReplayBackoffSeconds = 300,
        InitialReplayBackoffSeconds = 5,
        MaxRecordsPerUploadWindow = 5000
    };

    private static SiteConfigBuffer CreateDefaultBuffer() => new()
    {
        RetentionDays = 7,
        StalePendingDays = 3,
        MaxRecords = 30_000,
        CleanupIntervalHours = 24,
        PersistRawPayloads = false
    };

    private static SiteConfigLocalApi CreateDefaultLocalApi() => new()
    {
        LocalhostPort = 8585,
        EnableLanApi = false,
        LanAllowCidrs = [],
        RateLimitPerMinute = 60
    };

    private static SiteConfigTelemetry CreateDefaultTelemetry() => new()
    {
        TelemetryIntervalSeconds = 300,
        LogLevel = "Information",
        IncludeDiagnosticsLogs = false,
        MetricsWindowSeconds = 300
    };

    private static SiteConfigIdentity CreatePlaceholderIdentity() => new()
    {
        LegalEntityId = Guid.Empty,
        LegalEntityCode = string.Empty,
        SiteId = Guid.Empty,
        SiteCode = string.Empty,
        SiteName = string.Empty,
        Timezone = string.Empty,
        CurrencyCode = string.Empty,
        DeviceId = string.Empty,
        DeviceClass = "DESKTOP",
        IsPrimaryAgent = false
    };

    private static SiteConfigSite CreatePlaceholderSite() => new()
    {
        IsActive = true,
        OperatingModel = "COCO",
        SiteUsesPreAuth = false,
        ConnectivityMode = "CONNECTED",
        OdooSiteId = string.Empty,
        CompanyTaxPayerId = string.Empty
    };

    private static SiteConfigSiteHa CreateDefaultSiteHa() => new()
    {
        Enabled = false,
        AutoFailoverEnabled = false,
        Priority = 100,
        RoleCapability = "PRIMARY_ELIGIBLE",
        CurrentRole = "STANDBY_HOT",
        HeartbeatIntervalSeconds = 5,
        FailoverTimeoutSeconds = 30,
        MaxReplicationLagSeconds = 15,
        PeerDiscoveryMode = "HYBRID",
        AllowFailback = false,
        LeaderEpoch = 0,
        PeerDirectory = [],
        PeerApiPort = 8586,
        ReplicationEnabled = true,
        ProxyingEnabled = true
    };

    private static SiteConfigFiscalization CreateDefaultFiscalization() => new()
    {
        Mode = "NONE",
        RequireCustomerTaxId = false,
        FiscalReceiptRequired = false
    };

    private static SiteConfigMappings CreateDefaultMappings() => new()
    {
        PumpNumberOffset = 0,
        PriceDecimalPlaces = 2,
        VolumeUnit = "LITRE",
        Products = [],
        Nozzles = []
    };

    private static SiteConfigRollout CreateDefaultRollout() => new()
    {
        MinAgentVersion = "0.0.0",
        RequiresRestartSections = [],
        ConfigTtlHours = 24
    };
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

namespace FccDesktopAgent.Core.Config;

/// <summary>
/// Site configuration returned by <c>GET /api/v1/agent/config</c>.
/// This is the full cloud-managed configuration snapshot applied at runtime.
/// JSON property names are camelCase; deserialized with <c>PropertyNameCaseInsensitive = true</c>.
/// </summary>
public sealed class SiteConfig
{
    public string SchemaVersion { get; set; } = "1.0";
    public int ConfigVersion { get; set; }
    public string ConfigId { get; set; } = string.Empty;
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset EffectiveAtUtc { get; set; }
    public SiteConfigIdentity? Identity { get; set; }
    public SiteConfigSite? Site { get; set; }
    public SiteConfigFcc? Fcc { get; set; }
    public SiteConfigSync? Sync { get; set; }
    public SiteConfigBuffer? Buffer { get; set; }
    public SiteConfigLocalApi? LocalApi { get; set; }
    public SiteConfigTelemetry? Telemetry { get; set; }
    public SiteConfigFiscalization? Fiscalization { get; set; }
    public SiteConfigMappings? Mappings { get; set; }
    public SiteConfigRollout? Rollout { get; set; }
}

public sealed class SiteConfigIdentity
{
    public string DeviceId { get; set; } = string.Empty;
    public string SiteCode { get; set; } = string.Empty;
    public string LegalEntityId { get; set; } = string.Empty;
}

public sealed class SiteConfigSite
{
    public string? OperatingModel { get; set; }
    public string? ConnectivityMode { get; set; }
    public string? Timezone { get; set; }
    public string? Currency { get; set; }
}

public sealed class SiteConfigFcc
{
    public bool Enabled { get; set; }
    public string? FccId { get; set; }
    public string? Vendor { get; set; }
    public string? ConnectionProtocol { get; set; }
    public string? HostAddress { get; set; }
    public int? Port { get; set; }
    public string? CredentialRef { get; set; }
    public string? TransactionMode { get; set; }
    public string? IngestionMode { get; set; }
    public int? PullIntervalSeconds { get; set; }
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int HeartbeatTimeoutSeconds { get; set; } = 60;

    /// <summary>Petronite: local HTTP port for the webhook listener (default 8090).</summary>
    public int? WebhookListenerPort { get; set; }
}

public sealed class SiteConfigSync
{
    public string? CloudBaseUrl { get; set; }
    public int UploadBatchSize { get; set; } = 50;
    public int UploadIntervalSeconds { get; set; } = 60;
    public int ConfigPollIntervalSeconds { get; set; } = 60;
    public string? CursorStrategy { get; set; }

    /// <summary>
    /// Runtime certificate pins (SHA-256 public key hashes) for TLS certificate pinning.
    /// Format: "sha256/BASE64HASH=". Enables pin rotation without installer update.
    /// </summary>
    public List<string>? CertificatePins { get; set; }
}

public sealed class SiteConfigBuffer
{
    public int RetentionDays { get; set; } = 7;
    public int MaxRecords { get; set; } = 30_000;
    public int CleanupIntervalHours { get; set; } = 24;
    public bool PersistRawPayloads { get; set; }
}

public sealed class SiteConfigLocalApi
{
    public int LocalhostPort { get; set; } = 8585;
    public bool EnableLanApi { get; set; }
    public string? LanBindAddress { get; set; }
    public string? LanApiKeyRef { get; set; }
    public int RateLimitPerMinute { get; set; } = 60;
}

public sealed class SiteConfigTelemetry
{
    public int TelemetryIntervalSeconds { get; set; } = 300;
    public string LogLevel { get; set; } = "Information";
    public bool IncludeDiagnosticsLogs { get; set; }
    public int MetricsWindowSeconds { get; set; } = 300;
}

public sealed class SiteConfigFiscalization
{
    public string? Mode { get; set; }
    public string? TaxAuthorityEndpoint { get; set; }
    public bool RequireCustomerTaxId { get; set; }
    public bool FiscalReceiptRequired { get; set; }
}

public sealed class SiteConfigMappings
{
    public int PumpNumberOffset { get; set; }
    public int PriceDecimalPlaces { get; set; } = 2;
    public string VolumeUnit { get; set; } = "LITRE";
    public List<SiteConfigNozzleMapping> Nozzles { get; set; } = [];
}

public sealed class SiteConfigNozzleMapping
{
    public int OdooPumpNumber { get; set; }
    public int FccPumpNumber { get; set; }
    public int OdooNozzleNumber { get; set; }
    public int FccNozzleNumber { get; set; }
    public string ProductCode { get; set; } = string.Empty;
}

public sealed class SiteConfigRollout
{
    public string? MinAgentVersion { get; set; }
    public string? MaxAgentVersion { get; set; }
    public List<string> RequiresRestartSections { get; set; } = [];
    public int ConfigTtlHours { get; set; } = 24;
}

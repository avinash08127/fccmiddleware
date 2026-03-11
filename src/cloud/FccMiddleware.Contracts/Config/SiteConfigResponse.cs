using System.Text.Json.Serialization;

namespace FccMiddleware.Contracts.Config;

/// <summary>
/// Full site configuration delivered to Edge Agent on config pulls.
/// Matches schemas/config/site-config.schema.json.
/// </summary>
public sealed record SiteConfigResponse
{
    public required string SchemaVersion { get; init; }
    public required int ConfigVersion { get; init; }
    public required Guid ConfigId { get; init; }
    public required DateTimeOffset IssuedAtUtc { get; init; }
    public required DateTimeOffset EffectiveAtUtc { get; init; }
    public required SourceRevisionDto SourceRevision { get; init; }
    public required IdentityDto Identity { get; init; }
    public required SiteDto Site { get; init; }
    public required FccDto Fcc { get; init; }
    public required SyncDto Sync { get; init; }
    public required BufferDto Buffer { get; init; }
    public required LocalApiDto LocalApi { get; init; }
    public required TelemetryDto Telemetry { get; init; }
    public required FiscalizationDto Fiscalization { get; init; }
    public required MappingsDto Mappings { get; init; }
    public required RolloutDto Rollout { get; init; }
}

public sealed record SourceRevisionDto
{
    public DateTimeOffset? DatabricksSyncAtUtc { get; init; }
    public string? SiteMasterRevision { get; init; }
    public string? FccConfigRevision { get; init; }
    public string? PortalChangeId { get; init; }
}

public sealed record IdentityDto
{
    public required Guid LegalEntityId { get; init; }
    public required string LegalEntityCode { get; init; }
    public required Guid SiteId { get; init; }
    public required string SiteCode { get; init; }
    public required string SiteName { get; init; }
    public required string Timezone { get; init; }
    public required string CurrencyCode { get; init; }
    public required string DeviceId { get; init; }
    public required bool IsPrimaryAgent { get; init; }
}

public sealed record SiteDto
{
    public required bool IsActive { get; init; }
    public required string OperatingModel { get; init; }
    public required string ConnectivityMode { get; init; }
    public required string OdooSiteId { get; init; }
    public required string CompanyTaxPayerId { get; init; }
    public string? OperatorName { get; init; }
    public string? OperatorTaxPayerId { get; init; }
}

public sealed record FccDto
{
    public required bool Enabled { get; init; }
    public Guid? FccId { get; init; }
    public string? Vendor { get; init; }
    public string? Model { get; init; }
    public string? Version { get; init; }
    public string? ConnectionProtocol { get; init; }
    public string? HostAddress { get; init; }
    public int? Port { get; init; }
    public string? CredentialRef { get; init; }
    public int? CredentialRevision { get; init; }
    public required SecretEnvelopeDto SecretEnvelope { get; init; }
    public string? TransactionMode { get; init; }
    public string? IngestionMode { get; init; }
    public int? PullIntervalSeconds { get; init; }
    public int? CatchUpPullIntervalSeconds { get; init; }
    public int? HybridCatchUpIntervalSeconds { get; init; }
    public required int HeartbeatIntervalSeconds { get; init; }
    public required int HeartbeatTimeoutSeconds { get; init; }
    public required string[] PushSourceIpAllowList { get; init; }
}

public sealed record SecretEnvelopeDto
{
    public required string Format { get; init; }
    public string? Payload { get; init; }
}

public sealed record SyncDto
{
    public required string CloudBaseUrl { get; init; }
    public required int UploadBatchSize { get; init; }
    public required int UploadIntervalSeconds { get; init; }
    public required int SyncedStatusPollIntervalSeconds { get; init; }
    public required int ConfigPollIntervalSeconds { get; init; }
    public required string CursorStrategy { get; init; }
    public required int MaxReplayBackoffSeconds { get; init; }
    public required int InitialReplayBackoffSeconds { get; init; }
    public required int MaxRecordsPerUploadWindow { get; init; }
}

public sealed record BufferDto
{
    public required int RetentionDays { get; init; }
    public required int StalePendingDays { get; init; }
    public required int MaxRecords { get; init; }
    public required int CleanupIntervalHours { get; init; }
    public required bool PersistRawPayloads { get; init; }
}

public sealed record LocalApiDto
{
    public required int LocalhostPort { get; init; }
    public required bool EnableLanApi { get; init; }
    public string? LanBindAddress { get; init; }
    public required string[] LanAllowCidrs { get; init; }
    public string? LanApiKeyRef { get; init; }
    public required int RateLimitPerMinute { get; init; }
}

public sealed record TelemetryDto
{
    public required int TelemetryIntervalSeconds { get; init; }
    public required string LogLevel { get; init; }
    public required bool IncludeDiagnosticsLogs { get; init; }
    public required int MetricsWindowSeconds { get; init; }
}

public sealed record FiscalizationDto
{
    public required string Mode { get; init; }
    public string? TaxAuthorityEndpoint { get; init; }
    public required bool RequireCustomerTaxId { get; init; }
    public required bool FiscalReceiptRequired { get; init; }
}

public sealed record MappingsDto
{
    public required int PumpNumberOffset { get; init; }
    public required int PriceDecimalPlaces { get; init; }
    public required string VolumeUnit { get; init; }
    public required ProductMappingDto[] Products { get; init; }
    public required NozzleMappingDto[] Nozzles { get; init; }
}

public sealed record ProductMappingDto
{
    public required string FccProductCode { get; init; }
    public required string CanonicalProductCode { get; init; }
    public required string DisplayName { get; init; }
    public required bool Active { get; init; }
}

public sealed record NozzleMappingDto
{
    public required Guid PumpNozzleId { get; init; }
    public required int PumpNumber { get; init; }
    public required int NozzleNumber { get; init; }
    public required string CanonicalProductCode { get; init; }
    public string? OdooPumpId { get; init; }
    public required bool Active { get; init; }
}

public sealed record RolloutDto
{
    public required string MinAgentVersion { get; init; }
    public string? MaxAgentVersion { get; init; }
    public required string[] RequiresRestartSections { get; init; }
    public required int ConfigTtlHours { get; init; }
}

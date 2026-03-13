namespace FccMiddleware.Contracts.Portal;

public record SiteDto
{
    public required Guid Id { get; init; }
    public required string SiteCode { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required string SiteName { get; init; }
    public required string OperatingModel { get; init; }
    public required bool SiteUsesPreAuth { get; init; }
    public string? ConnectivityMode { get; init; }
    public string? IngestionMode { get; init; }
    public string? FccVendor { get; init; }
    public string? Timezone { get; init; }
    public required bool IsActive { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public record SiteDetailDto : SiteDto
{
    public string? OperatorName { get; init; }
    public FccConfigurationDto? Fcc { get; init; }
    public SiteFiscalizationDto? Fiscalization { get; init; }
    public SiteToleranceDto? Tolerance { get; init; }
    public required IReadOnlyList<PumpDto> Pumps { get; init; }
}

public sealed record SiteToleranceDto
{
    public required decimal AmountTolerancePct { get; init; }
    public required long AmountToleranceAbsoluteMinorUnits { get; init; }
    public required int TimeWindowMinutes { get; init; }
}

public sealed record SiteFiscalizationDto
{
    public required string Mode { get; init; }
    public string? TaxAuthorityEndpoint { get; init; }
    public required bool RequireCustomerTaxId { get; init; }
    public required bool FiscalReceiptRequired { get; init; }
}

public sealed record PumpDto
{
    public required Guid Id { get; init; }
    public required string SiteCode { get; init; }
    public required int PumpNumber { get; init; }
    public required IReadOnlyList<NozzleDto> Nozzles { get; init; }
    public required bool IsActive { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record NozzleDto
{
    public required int NozzleNumber { get; init; }
    public required string CanonicalProductCode { get; init; }
    public string? OdooPumpId { get; init; }
}

public sealed record ProductDto
{
    public required Guid Id { get; init; }
    public required string CanonicalCode { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsActive { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record FccConfigurationDto
{
    public required bool Enabled { get; init; }
    public string? FccId { get; init; }
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
    public required IReadOnlyList<string> PushSourceIpAllowList { get; init; }

    // F10-01: DOMS TCP/JPL vendor-specific fields
    public int? JplPort { get; init; }
    public string? FcAccessCode { get; init; }
    public string? DomsCountryCode { get; init; }
    public string? PosVersionId { get; init; }
    public string? ConfiguredPumps { get; init; }

    // F10-01: Radix vendor-specific fields
    public string? SharedSecret { get; init; }
    public int? UsnCode { get; init; }
    public int? AuthPort { get; init; }
    public string? FccPumpAddressMap { get; init; }

    // F10-01: Petronite OAuth2 vendor-specific fields
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? WebhookSecret { get; init; }
    public string? OAuthTokenEndpoint { get; init; }

    // F10-01: Advatec EFD vendor-specific fields
    public int? AdvatecDevicePort { get; init; }
    public string? AdvatecWebhookToken { get; init; }
    public string? AdvatecEfdSerialNumber { get; init; }
    public int? AdvatecCustIdType { get; init; }
    public string? AdvatecPumpMap { get; init; }
}

public sealed record SecretEnvelopeDto
{
    public required string Format { get; init; }
    public string? Payload { get; init; }
}

public sealed record UpdateSiteRequestDto
{
    public string? ConnectivityMode { get; init; }
    public string? OperatingModel { get; init; }
    public bool? SiteUsesPreAuth { get; init; }
    public SiteTolerancePatchDto? Tolerance { get; init; }
    public SiteFiscalizationPatchDto? Fiscalization { get; init; }
}

public sealed record SiteTolerancePatchDto
{
    public decimal? AmountTolerancePct { get; init; }
    public long? AmountToleranceAbsoluteMinorUnits { get; init; }
    public int? TimeWindowMinutes { get; init; }
}

public sealed record SiteFiscalizationPatchDto
{
    public string? Mode { get; init; }
    public string? TaxAuthorityEndpoint { get; init; }
    public bool? RequireCustomerTaxId { get; init; }
    public bool? FiscalReceiptRequired { get; init; }
}

public sealed record UpdateFccConfigRequestDto
{
    public bool? Enabled { get; init; }
    public string? Vendor { get; init; }
    public string? ConnectionProtocol { get; init; }
    public string? HostAddress { get; init; }
    public int? Port { get; init; }
    public string? TransactionMode { get; init; }
    public string? IngestionMode { get; init; }
    public int? PullIntervalSeconds { get; init; }
    public int? CatchUpPullIntervalSeconds { get; init; }
    public int? HybridCatchUpIntervalSeconds { get; init; }
    public int? HeartbeatIntervalSeconds { get; init; }
    public int? HeartbeatTimeoutSeconds { get; init; }

    // F10-01: DOMS TCP/JPL vendor-specific fields
    public int? JplPort { get; init; }
    public string? FcAccessCode { get; init; }
    public string? DomsCountryCode { get; init; }
    public string? PosVersionId { get; init; }
    public string? ConfiguredPumps { get; init; }

    // F10-01: Radix vendor-specific fields
    public string? SharedSecret { get; init; }
    public int? UsnCode { get; init; }
    public int? AuthPort { get; init; }
    public string? FccPumpAddressMap { get; init; }

    // F10-01: Petronite OAuth2 vendor-specific fields
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? WebhookSecret { get; init; }
    public string? OAuthTokenEndpoint { get; init; }

    // F10-01: Advatec EFD vendor-specific fields
    public int? AdvatecDevicePort { get; init; }
    public string? AdvatecWebhookToken { get; init; }
    public string? AdvatecEfdSerialNumber { get; init; }
    public int? AdvatecCustIdType { get; init; }
    public string? AdvatecPumpMap { get; init; }
}

public sealed record AddPumpRequestDto
{
    public required int PumpNumber { get; init; }
    public required int FccPumpNumber { get; init; }
    public required IReadOnlyList<AddNozzleRequestDto> Nozzles { get; init; }
}

public sealed record AddNozzleRequestDto
{
    public required int NozzleNumber { get; init; }
    public required int FccNozzleNumber { get; init; }
    public required string CanonicalProductCode { get; init; }
}

public sealed record UpdateNozzleRequestDto
{
    public required string CanonicalProductCode { get; init; }
}

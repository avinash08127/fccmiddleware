using System.Text.Json;

namespace FccMiddleware.Contracts.Portal;

public sealed record AdapterSummaryDto
{
    public required string AdapterKey { get; init; }
    public required string DisplayName { get; init; }
    public required string Vendor { get; init; }
    public required string AdapterVersion { get; init; }
    public required IReadOnlyList<string> SupportedProtocols { get; init; }
    public required IReadOnlyList<string> SupportedIngestionMethods { get; init; }
    public required bool SupportsPreAuth { get; init; }
    public required bool SupportsPumpStatus { get; init; }
    public required int ActiveSiteCount { get; init; }
    public required int DefaultConfigVersion { get; init; }
    public DateTimeOffset? DefaultUpdatedAt { get; init; }
    public string? DefaultUpdatedBy { get; init; }
}

public sealed record AdapterFieldOptionDto
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}

public sealed record AdapterFieldDefinitionDto
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Type { get; init; }
    public required string Group { get; init; }
    public required bool Required { get; init; }
    public required bool Sensitive { get; init; }
    public required bool Defaultable { get; init; }
    public required bool SiteConfigurable { get; init; }
    public string? Description { get; init; }
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public string? VisibleWhenKey { get; init; }
    public string? VisibleWhenValue { get; init; }
    public IReadOnlyList<AdapterFieldOptionDto>? Options { get; init; }
}

public sealed record AdapterSchemaDto
{
    public required string AdapterKey { get; init; }
    public required string DisplayName { get; init; }
    public required string Vendor { get; init; }
    public required string AdapterVersion { get; init; }
    public required IReadOnlyList<string> SupportedProtocols { get; init; }
    public required IReadOnlyList<string> SupportedIngestionMethods { get; init; }
    public required bool SupportsPreAuth { get; init; }
    public required bool SupportsPumpStatus { get; init; }
    public required IReadOnlyList<AdapterFieldDefinitionDto> Fields { get; init; }
}

public sealed record AdapterConfigDocumentDto
{
    public required string AdapterKey { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required int ConfigVersion { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
    public required JsonElement Values { get; init; }
    public required JsonElement SecretState { get; init; }
}

public sealed record AdapterSiteUsageDto
{
    public required Guid SiteId { get; init; }
    public required string SiteCode { get; init; }
    public required string SiteName { get; init; }
    public required bool HasOverride { get; init; }
    public int? OverrideVersion { get; init; }
    public DateTimeOffset? OverrideUpdatedAt { get; init; }
    public string? OverrideUpdatedBy { get; init; }
}

public sealed record AdapterDetailDto
{
    public required AdapterSchemaDto Schema { get; init; }
    public required AdapterConfigDocumentDto DefaultConfig { get; init; }
    public required IReadOnlyList<AdapterSiteUsageDto> Sites { get; init; }
}

public sealed record SiteAdapterConfigDto
{
    public required Guid SiteId { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required string SiteCode { get; init; }
    public required string SiteName { get; init; }
    public required string AdapterKey { get; init; }
    public required string Vendor { get; init; }
    public required int DefaultConfigVersion { get; init; }
    public int? OverrideVersion { get; init; }
    public DateTimeOffset? OverrideUpdatedAt { get; init; }
    public string? OverrideUpdatedBy { get; init; }
    public required JsonElement DefaultValues { get; init; }
    public required JsonElement OverrideValues { get; init; }
    public required JsonElement EffectiveValues { get; init; }
    public required JsonElement SecretState { get; init; }
    public required JsonElement FieldSources { get; init; }
    public required AdapterSchemaDto Schema { get; init; }
}

public sealed record UpdateAdapterDefaultConfigRequestDto
{
    public required Guid LegalEntityId { get; init; }
    public required string Reason { get; init; }
    public required JsonElement Values { get; init; }
}

public sealed record UpdateSiteAdapterConfigRequestDto
{
    public required string Reason { get; init; }
    public required JsonElement EffectiveValues { get; init; }
}

public sealed record ResetSiteAdapterConfigRequestDto
{
    public required string Reason { get; init; }
}

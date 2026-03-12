namespace FccMiddleware.Contracts.Portal;

public sealed record PortalLegalEntityDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string CountryCode { get; init; }
    public required string CountryName { get; init; }
    public required string CurrencyCode { get; init; }
    public string? Country { get; init; }
    public required string OdooCompanyId { get; init; }
    public required bool IsActive { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record MasterDataSyncStatusDto
{
    public required string EntityType { get; init; }
    public DateTimeOffset? LastSyncAtUtc { get; init; }
    public required int UpsertedCount { get; init; }
    public required int DeactivatedCount { get; init; }
    public required int ErrorCount { get; init; }
    public required bool IsStale { get; init; }
    public required int StaleThresholdHours { get; init; }
}

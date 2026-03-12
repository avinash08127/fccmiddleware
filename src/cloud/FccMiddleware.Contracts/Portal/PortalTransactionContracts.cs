namespace FccMiddleware.Contracts.Portal;

public sealed record PortalTransactionDto
{
    public required Guid Id { get; init; }
    public required string FccTransactionId { get; init; }
    public required string SiteCode { get; init; }
    public required int PumpNumber { get; init; }
    public required int NozzleNumber { get; init; }
    public required string ProductCode { get; init; }
    public required long VolumeMicrolitres { get; init; }
    public required long AmountMinorUnits { get; init; }
    public required long UnitPriceMinorPerLitre { get; init; }
    public required string CurrencyCode { get; init; }
    public required string Status { get; init; }
    public string? ReconciliationStatus { get; init; }
    public required string IngestionSource { get; init; }
    public required string FccVendor { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required DateTimeOffset IngestedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required int SchemaVersion { get; init; }
    public required bool IsDuplicate { get; init; }
    public Guid? DuplicateOfId { get; init; }
    public Guid? PreAuthId { get; init; }
    public string? OdooOrderId { get; init; }
    public string? FiscalReceiptNumber { get; init; }
    public string? AttendantId { get; init; }
    public string? RawPayloadRef { get; init; }
    public string? RawPayloadJson { get; init; }
    public required bool IsStale { get; init; }
}

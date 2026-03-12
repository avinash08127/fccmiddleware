namespace FccMiddleware.Contracts.Reconciliation;

public sealed record ReconciliationPreAuthSummaryDto
{
    public DateTimeOffset? RequestedAt { get; init; }
    public string? VehicleNumber { get; init; }
    public string? CustomerBusinessName { get; init; }
    public string? AttendantId { get; init; }
    public string? FccCorrelationId { get; init; }
    public string? FccAuthorizationCode { get; init; }
}

public sealed record ReconciliationTransactionSummaryDto
{
    public required string FccTransactionId { get; init; }
    public long? VolumeMicrolitres { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record ReconciliationRecordDto
{
    public required Guid Id { get; init; }
    public Guid? PreAuthId { get; init; }
    public Guid? TransactionId { get; init; }
    public required string SiteCode { get; init; }
    public required Guid LegalEntityId { get; init; }
    public string? OdooOrderId { get; init; }
    public required int PumpNumber { get; init; }
    public required int NozzleNumber { get; init; }
    public string? ProductCode { get; init; }
    public string? CurrencyCode { get; init; }
    public long? RequestedAmount { get; init; }
    public long? ActualAmount { get; init; }
    public long? AmountVariance { get; init; }
    public decimal? VarianceBps { get; init; }
    public string? MatchMethod { get; init; }
    public required bool AmbiguityFlag { get; init; }
    public string? PreAuthStatus { get; init; }
    public required string ReconciliationStatus { get; init; }
    public string? Decision { get; init; }
    public string? DecisionReason { get; init; }
    public string? DecidedBy { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public ReconciliationPreAuthSummaryDto? PreAuthSummary { get; init; }
    public ReconciliationTransactionSummaryDto? TransactionSummary { get; init; }
}

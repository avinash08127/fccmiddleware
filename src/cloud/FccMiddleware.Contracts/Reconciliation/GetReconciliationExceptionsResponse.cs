namespace FccMiddleware.Contracts.Reconciliation;

public sealed record ReconciliationExceptionDto
{
    public required Guid Id { get; init; }
    public required Guid ReconciliationId { get; init; }
    public Guid? PreAuthId { get; init; }
    public Guid? TransactionId { get; init; }
    public required string Status { get; init; }
    public required string SiteCode { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required int PumpNumber { get; init; }
    public required int NozzleNumber { get; init; }
    public string? OdooOrderId { get; init; }
    public string? CurrencyCode { get; init; }
    public long? RequestedAmount { get; init; }
    public long? ActualAmount { get; init; }
    public long? AmountVariance { get; init; }
    public decimal? VarianceBps { get; init; }
    public long? AuthorizedAmountMinorUnits { get; init; }
    public long? ActualAmountMinorUnits { get; init; }
    public long? VarianceMinorUnits { get; init; }
    public decimal? VariancePercent { get; init; }
    public required string MatchMethod { get; init; }
    public required bool AmbiguityFlag { get; init; }
    public string? Decision { get; init; }
    public string? DecisionReason { get; init; }
    public string? DecidedBy { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required DateTimeOffset LastMatchAttemptAt { get; init; }
}

public sealed record GetReconciliationExceptionsResponse
{
    public required IReadOnlyList<ReconciliationExceptionDto> Data { get; init; }
    public required ReconciliationExceptionPageMeta Meta { get; init; }
}

public sealed record ReconciliationExceptionPageMeta
{
    public required int PageSize { get; init; }
    public required bool HasMore { get; init; }
    public string? NextCursor { get; init; }
    public int? TotalCount { get; init; }
}

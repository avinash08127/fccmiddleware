namespace FccMiddleware.Contracts.Reconciliation;

public sealed record ReconciliationExceptionDto
{
    public required Guid ReconciliationId { get; init; }
    public required string Status { get; init; }
    public required string SiteCode { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required int PumpNumber { get; init; }
    public required int NozzleNumber { get; init; }
    public long? AuthorizedAmountMinorUnits { get; init; }
    public required long ActualAmountMinorUnits { get; init; }
    public long? VarianceMinorUnits { get; init; }
    public decimal? VariancePercent { get; init; }
    public required string MatchMethod { get; init; }
    public required bool AmbiguityFlag { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
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
}

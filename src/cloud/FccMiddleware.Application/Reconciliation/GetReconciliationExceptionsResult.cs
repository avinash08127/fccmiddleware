namespace FccMiddleware.Application.Reconciliation;

public sealed record ReconciliationExceptionListItem(
    Guid ReconciliationId,
    Domain.Enums.ReconciliationStatus Status,
    string SiteCode,
    Guid LegalEntityId,
    int PumpNumber,
    int NozzleNumber,
    long? AuthorizedAmountMinorUnits,
    long ActualAmountMinorUnits,
    long? VarianceMinorUnits,
    decimal? VariancePercent,
    string MatchMethod,
    bool AmbiguityFlag,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastMatchAttemptAt);

public sealed record GetReconciliationExceptionsResult
{
    public required IReadOnlyList<ReconciliationExceptionListItem> Records { get; init; }
    public required bool HasMore { get; init; }
    public string? NextCursor { get; init; }
}

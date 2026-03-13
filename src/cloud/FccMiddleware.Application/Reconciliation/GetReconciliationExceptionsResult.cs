namespace FccMiddleware.Application.Reconciliation;

public sealed record ReconciliationExceptionListItem(
    Guid ReconciliationId,
    Guid? PreAuthId,
    Guid TransactionId,
    Domain.Enums.ReconciliationStatus Status,
    string SiteCode,
    Guid LegalEntityId,
    int PumpNumber,
    int NozzleNumber,
    string? OdooOrderId,
    string? CurrencyCode,
    long? RequestedAmount,
    long? AuthorizedAmountMinorUnits,
    long ActualAmountMinorUnits,
    long? VarianceMinorUnits,
    decimal? VariancePercent,
    string? ReviewReason,
    string? ReviewedByUserId,
    DateTimeOffset? ReviewedAtUtc,
    DateTimeOffset UpdatedAt,
    string MatchMethod,
    bool AmbiguityFlag,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastMatchAttemptAt);

public sealed record GetReconciliationExceptionsResult
{
    public required IReadOnlyList<ReconciliationExceptionListItem> Records { get; init; }
    public required bool HasMore { get; init; }
    public int? TotalCount { get; init; }
    public string? NextCursor { get; init; }
}

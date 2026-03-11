namespace FccMiddleware.Application.Transactions;

/// <summary>Result of a <see cref="GetSyncedTransactionIdsQuery"/>.</summary>
public sealed record GetSyncedTransactionIdsResult
{
    public required IReadOnlyList<string> FccTransactionIds { get; init; }
}

using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Application.Transactions;

/// <summary>
/// Application-layer data access contract for the Odoo acknowledge pipeline.
/// Implemented by FccMiddlewareDbContext in Infrastructure.
/// Keeps the Application project free of EF Core package references.
/// </summary>
public interface IAcknowledgeTransactionsDbContext
{
    /// <summary>
    /// Fetches all transactions matching the given IDs within the specified tenant scope.
    /// Uses IgnoreQueryFilters so the caller explicitly controls tenant isolation.
    /// </summary>
    Task<List<Transaction>> FindTransactionsByIdsAsync(
        IReadOnlyList<Guid> ids,
        Guid legalEntityId,
        CancellationToken ct = default);

    /// <summary>Stages a new OutboxMessage for persistence. Committed by SaveChangesAsync.</summary>
    void AddOutboxMessage(OutboxMessage message);

    /// <summary>Commits all staged changes atomically.</summary>
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

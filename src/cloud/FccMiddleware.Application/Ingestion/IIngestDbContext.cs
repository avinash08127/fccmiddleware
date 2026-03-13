using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Application.Ingestion;

/// <summary>
/// Application-layer data access contract for the ingestion pipeline.
/// Implemented by FccMiddlewareDbContext (via a thin adapter) in Infrastructure.
/// Keeps the Application project free of EF Core package references.
/// </summary>
public interface IIngestDbContext
{
    /// <summary>Stages a new Transaction for persistence. Committed by SaveChangesAsync.</summary>
    void AddTransaction(Transaction transaction);

    /// <summary>Stages a new OutboxMessage for persistence. Committed by SaveChangesAsync.</summary>
    void AddOutboxMessage(OutboxMessage message);

    /// <summary>Commits all staged changes atomically.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards all staged (unsaved) changes. Used on conflict recovery.</summary>
    void ClearTracked();

    /// <summary>
    /// Looks up the ID of an existing transaction by the primary dedup key, bypassing tenant filter.
    /// Returns null when no matching record exists within the active dedup window.
    /// </summary>
    Task<Guid?> FindTransactionByDedupKeyAsync(
        string fccTransactionId,
        string siteCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets ReconciliationStatus = REVIEW_FUZZY_MATCH on the transaction with the given ID,
    /// if it is not already set. Used to propagate fuzzy match flags during dedup races.
    /// </summary>
    Task FlagFuzzyMatchAsync(Guid transactionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Secondary fuzzy match: checks whether a non-duplicate transaction at the same pump + nozzle
    /// with the same amount exists within a ±window of completedAt.
    /// Matches against all statuses except DUPLICATE so that recently acknowledged (SYNCED_TO_ODOO)
    /// transactions are still detected as potential fuzzy duplicates.
    /// </summary>
    Task<bool> HasFuzzyMatchAsync(
        Guid legalEntityId,
        string siteCode,
        int pumpNumber,
        int nozzleNumber,
        long amountMinorUnits,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken = default);
}

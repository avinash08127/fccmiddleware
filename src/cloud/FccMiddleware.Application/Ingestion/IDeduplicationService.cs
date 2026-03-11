namespace FccMiddleware.Application.Ingestion;

/// <summary>
/// Two-tier deduplication service: Redis cache (fast path) with PostgreSQL fallback.
/// Implements the primary dedup check per tier-2-2-deduplication-strategy.md §3.1.
/// </summary>
public interface IDeduplicationService
{
    /// <summary>
    /// Checks whether a transaction with the given dedup key already exists.
    /// Checks Redis first; falls back to PostgreSQL within the configured window.
    /// Returns the original transaction's ID when a match is found, null otherwise.
    /// </summary>
    Task<Guid?> FindExistingAsync(string fccTransactionId, string siteCode, CancellationToken ct = default);

    /// <summary>
    /// Populates the Redis cache entry for a successfully ingested transaction.
    /// TTL is set to <paramref name="dedupWindowDays"/> days.
    /// Must be called AFTER the transaction has been persisted to PostgreSQL.
    /// </summary>
    Task SetCacheAsync(string fccTransactionId, string siteCode, Guid transactionId, int dedupWindowDays, CancellationToken ct = default);
}

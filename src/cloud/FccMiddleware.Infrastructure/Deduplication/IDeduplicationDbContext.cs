namespace FccMiddleware.Infrastructure.Deduplication;

/// <summary>
/// Minimal DB surface used by RedisDeduplicationService for the PostgreSQL fallback query.
/// Implemented by FccMiddlewareDbContext.
/// </summary>
public interface IDeduplicationDbContext
{
    /// <summary>
    /// Returns the ID of an existing transaction matching the primary dedup key,
    /// ignoring tenant query filters so the lookup works regardless of request context.
    /// Returns null when no match is found.
    /// </summary>
    Task<Guid?> FindTransactionIdByDedupKeyAsync(
        string fccTransactionId,
        string siteCode,
        CancellationToken cancellationToken = default);
}

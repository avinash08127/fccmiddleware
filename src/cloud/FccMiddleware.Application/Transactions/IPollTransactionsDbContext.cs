namespace FccMiddleware.Application.Transactions;

/// <summary>
/// Read-only query interface for the Odoo poll endpoint.
/// Implemented by Infrastructure (EF Core); the Application layer calls it without
/// a direct dependency on Entity Framework Core.
/// </summary>
public interface IPollTransactionsDbContext
{
    /// <summary>
    /// Returns a page of PENDING transactions for the specified legal entity,
    /// ordered ascending by (createdAt, id) for deterministic cursor pagination.
    ///
    /// The implementation applies the <c>ix_transactions_odoo_poll</c> partial index via the
    /// <c>legal_entity_id / status = 'PENDING'</c> predicate.
    ///
    /// Passing <paramref name="cursorCreatedAt"/> and <paramref name="cursorId"/> advances
    /// the window past the last row of the previous page (keyset pagination).
    /// </summary>
    Task<List<PollTransactionRecord>> FetchPendingPageAsync(
        Guid legalEntityId,
        string? siteCode,
        int? pumpNumber,
        DateTimeOffset? from,
        DateTimeOffset? cursorCreatedAt,
        Guid? cursorId,
        int take,
        CancellationToken ct = default);
}

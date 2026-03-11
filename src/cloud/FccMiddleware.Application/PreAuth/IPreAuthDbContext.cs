using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Application.PreAuth;

/// <summary>
/// Application-layer data access contract for the pre-auth forward pipeline.
/// Implemented by FccMiddlewareDbContext in Infrastructure.
/// </summary>
public interface IPreAuthDbContext
{
    /// <summary>
    /// Finds an existing pre-auth record by the dedup key (odooOrderId, siteCode).
    /// Returns null when no matching record exists.
    /// </summary>
    Task<PreAuthRecord?> FindByDedupKeyAsync(
        string odooOrderId,
        string siteCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a pre-auth record by middleware ID within the given tenant.
    /// Returns null when no matching record exists.
    /// </summary>
    Task<PreAuthRecord?> FindByIdAsync(
        Guid preAuthId,
        Guid legalEntityId,
        CancellationToken cancellationToken = default);

    /// <summary>Stages a new PreAuthRecord for persistence.</summary>
    void AddPreAuthRecord(PreAuthRecord record);

    /// <summary>Stages a new OutboxMessage for persistence.</summary>
    void AddOutboxMessage(OutboxMessage message);

    /// <summary>Commits all staged changes atomically.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards all staged (unsaved) changes. Used on conflict recovery.</summary>
    void ClearTracked();
}

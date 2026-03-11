using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Application.Reconciliation;

public interface IReconciliationDbContext
{
    Task<ReconciliationRecord?> FindByIdAsync(Guid reconciliationId, CancellationToken ct);

    Task<ReconciliationRecord?> FindByTransactionIdAsync(Guid transactionId, CancellationToken ct);

    Task<Transaction?> FindTransactionByIdAsync(Guid transactionId, CancellationToken ct);

    Task<List<ReconciliationExceptionListItem>> FetchExceptionsPageAsync(
        Guid? legalEntityId,
        IReadOnlyCollection<Guid> scopedLegalEntityIds,
        bool allowAllLegalEntities,
        string? siteCode,
        IReadOnlyCollection<FccMiddleware.Domain.Enums.ReconciliationStatus> statuses,
        DateTimeOffset? since,
        DateTimeOffset? cursorCreatedAt,
        Guid? cursorId,
        int take,
        CancellationToken ct);

    Task<List<ReconciliationRetryWorkItem>> FindDueUnmatchedRetriesAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken ct);

    Task<ReconciliationSiteContext?> FindSiteContextAsync(
        Guid legalEntityId,
        string siteCode,
        ReconciliationOptions defaults,
        CancellationToken ct);

    Task<List<PreAuthRecord>> FindCorrelationCandidatesAsync(
        Guid legalEntityId,
        string siteCode,
        string fccCorrelationId,
        CancellationToken ct);

    Task<List<PreAuthRecord>> FindPumpNozzleTimeCandidatesAsync(
        Guid legalEntityId,
        string siteCode,
        int pumpNumber,
        int nozzleNumber,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken ct);

    Task<List<PreAuthRecord>> FindOdooOrderCandidatesAsync(
        Guid legalEntityId,
        string siteCode,
        string odooOrderId,
        CancellationToken ct);

    void AddReconciliationRecord(ReconciliationRecord record);

    Task<int> SaveChangesAsync(CancellationToken ct);
}

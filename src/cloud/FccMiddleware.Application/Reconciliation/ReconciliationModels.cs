using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Application.Reconciliation;

public sealed record ReconciliationSettings(
    bool SiteUsesPreAuth,
    decimal AmountTolerancePercent,
    long AmountToleranceAbsolute,
    int TimeWindowMinutes);

public sealed record ReconciliationSiteContext(
    Guid LegalEntityId,
    string SiteCode,
    ReconciliationSettings Settings);

public sealed record ReconciliationMatchResult(
    bool Skipped,
    bool CreatedOrUpdated,
    ReconciliationStatus? Status,
    Guid? ReconciliationId);

public sealed record ReconciliationRetryWorkItem(
    ReconciliationRecord Reconciliation,
    Transaction Transaction);

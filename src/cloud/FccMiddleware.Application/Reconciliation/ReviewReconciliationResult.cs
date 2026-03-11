using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Application.Reconciliation;

public sealed record ReviewReconciliationResult(
    Guid ReconciliationId,
    ReconciliationStatus Status,
    Guid LegalEntityId,
    string SiteCode,
    string ReviewedByUserId,
    DateTimeOffset ReviewedAtUtc,
    string ReviewReason);

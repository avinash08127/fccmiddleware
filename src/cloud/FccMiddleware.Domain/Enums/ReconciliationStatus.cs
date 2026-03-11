namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Reconciliation outcome for a transaction at a pre-auth site.
/// Stored as SCREAMING_SNAKE_CASE string in PostgreSQL.
/// </summary>
public enum ReconciliationStatus
{
    UNMATCHED,
    MATCHED,
    VARIANCE_WITHIN_TOLERANCE,
    VARIANCE_FLAGGED,
    APPROVED,
    REJECTED
}

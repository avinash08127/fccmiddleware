namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Lifecycle status for a pre-authorization record.
/// Stored as SCREAMING_SNAKE_CASE string in PostgreSQL.
/// </summary>
public enum PreAuthStatus
{
    PENDING,
    AUTHORIZED,
    DISPENSING,
    COMPLETED,
    CANCELLED,
    EXPIRED,
    FAILED
}

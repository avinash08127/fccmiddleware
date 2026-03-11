namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Cloud-side lifecycle status for an ingested transaction.
/// Stored as SCREAMING_SNAKE_CASE string in PostgreSQL.
/// </summary>
public enum TransactionStatus
{
    PENDING,
    SYNCED_TO_ODOO,
    DUPLICATE,
    ARCHIVED
}

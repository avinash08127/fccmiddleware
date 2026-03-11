namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Cloud ingestion path for a site's FCC transactions.
/// Stored as SCREAMING_SNAKE_CASE string in PostgreSQL.
/// </summary>
public enum IngestionMode
{
    CLOUD_DIRECT,
    RELAY,
    BUFFER_ALWAYS
}

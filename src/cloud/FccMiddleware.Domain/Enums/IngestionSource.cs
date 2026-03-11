namespace FccMiddleware.Domain.Enums;

/// <summary>
/// How a transaction arrived at the cloud backend.
/// Stored as SCREAMING_SNAKE_CASE string in PostgreSQL.
/// </summary>
public enum IngestionSource
{
    FCC_PUSH,
    EDGE_UPLOAD,
    CLOUD_PULL
}

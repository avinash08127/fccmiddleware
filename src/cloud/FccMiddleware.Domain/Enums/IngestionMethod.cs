namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Physical method by which transactions flow from the FCC to the middleware.
/// Stored as SCREAMING_SNAKE_CASE string in PostgreSQL.
/// </summary>
public enum IngestionMethod
{
    PUSH,
    PULL,
    HYBRID
}

namespace FccMiddleware.Domain.Enums;

/// <summary>
/// How transactions flow from the FCC to the middleware.
/// Stored as SCREAMING_SNAKE_CASE string in PostgreSQL.
/// </summary>
public enum TransactionMode
{
    PUSH,
    PULL,
    HYBRID
}

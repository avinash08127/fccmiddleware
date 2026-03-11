namespace FccMiddleware.Domain.Enums;

/// <summary>
/// How fiscal receipts are generated for transactions at a site.
/// Stored as SCREAMING_SNAKE_CASE string in PostgreSQL.
/// </summary>
public enum FiscalizationMode
{
    /// <summary>The FCC issues fiscal receipts directly.</summary>
    FCC_DIRECT,
    /// <summary>An external system handles fiscalization.</summary>
    EXTERNAL_INTEGRATION,
    /// <summary>No fiscalization required at this site.</summary>
    NONE
}

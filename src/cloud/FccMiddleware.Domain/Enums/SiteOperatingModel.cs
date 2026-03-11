namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Site operating model (ownership/management structure).
/// Stored as SCREAMING_SNAKE_CASE string in PostgreSQL.
/// </summary>
public enum SiteOperatingModel
{
    /// <summary>Company Owned, Company Operated</summary>
    COCO,
    /// <summary>Company Owned, Dealer Operated</summary>
    CODO,
    /// <summary>Dealer Owned, Dealer Operated</summary>
    DODO,
    /// <summary>Dealer Owned, Company Operated</summary>
    DOCO
}

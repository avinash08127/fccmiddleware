namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Join entity linking a portal user to the legal entities they can access.
/// </summary>
public class PortalUserLegalEntity
{
    public Guid UserId { get; set; }
    public Guid LegalEntityId { get; set; }

    // Navigation
    public PortalUser User { get; set; } = null!;
    public LegalEntity LegalEntity { get; set; } = null!;
}

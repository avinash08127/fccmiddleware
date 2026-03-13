namespace FccMiddleware.Domain.Entities;

/// <summary>
/// A portal user linked to an Azure Entra identity via email.
/// Roles are maintained locally because Entra tokens do not carry role claims.
/// </summary>
public class PortalUser
{
    public Guid Id { get; set; }

    /// <summary>Primary lookup key — matches the email/preferred_username claim from Entra.</summary>
    public string Email { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    /// <summary>Azure Entra "oid" claim — auto-populated on first login. Optional.</summary>
    public string? EntraObjectId { get; set; }

    public short RoleId { get; set; }

    /// <summary>When true the user can access all legal entities (super-admin).</summary>
    public bool AllLegalEntities { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Email of the admin who created this user.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Email of the admin who last updated this user.</summary>
    public string? UpdatedBy { get; set; }

    // Navigation
    public PortalRole Role { get; set; } = null!;
    public ICollection<PortalUserLegalEntity> LegalEntityLinks { get; set; } = [];
}

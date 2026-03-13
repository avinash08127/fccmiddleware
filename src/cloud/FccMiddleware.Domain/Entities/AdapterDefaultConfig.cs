using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Legal-entity-scoped default adapter configuration profile.
/// Stores only backend-managed adapter defaults, not per-site unique settings.
/// </summary>
public class AdapterDefaultConfig : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid LegalEntityId { get; set; }
    public string AdapterKey { get; set; } = null!;
    public FccVendor FccVendor { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public int ConfigVersion { get; set; } = 1;
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public LegalEntity LegalEntity { get; set; } = null!;
}

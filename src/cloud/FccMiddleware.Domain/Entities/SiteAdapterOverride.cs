using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Site-level overrides for adapter defaults.
/// Only fields that differ from the legal-entity default profile are stored.
/// </summary>
public class SiteAdapterOverride : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string AdapterKey { get; set; } = null!;
    public FccVendor FccVendor { get; set; }
    public string OverrideJson { get; set; } = "{}";
    public int ConfigVersion { get; set; } = 1;
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Site Site { get; set; } = null!;
    public LegalEntity LegalEntity { get; set; } = null!;
}

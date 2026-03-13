using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// A fuel retail site (station). Site codes are globally unique (BR-2.3).
/// Tenant-scoped via LegalEntityId.
/// </summary>
public class Site : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = null!;
    public string SiteName { get; set; } = null!;
    public SiteOperatingModel OperatingModel { get; set; }
    public bool SiteUsesPreAuth { get; set; }
    public decimal? AmountTolerancePercent { get; set; }
    public long? AmountToleranceAbsolute { get; set; }
    public int? TimeWindowMinutes { get; set; }
    public string ConnectivityMode { get; set; } = "CONNECTED";
    public string? OperatorName { get; set; }
    public string? OperatorTaxPayerId { get; set; }
    public string CompanyTaxPayerId { get; set; } = null!;
    public FiscalizationMode FiscalizationMode { get; set; } = FiscalizationMode.NONE;
    public string? TaxAuthorityEndpoint { get; set; }
    public bool RequireCustomerTaxId { get; set; }
    public bool FiscalReceiptRequired { get; set; }
    public string? OdooSiteId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? DeactivatedAt { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation properties
    public LegalEntity LegalEntity { get; set; } = null!;
    public ICollection<Pump> Pumps { get; set; } = [];
    public ICollection<FccConfig> FccConfigs { get; set; } = [];
    public ICollection<AgentRegistration> AgentRegistrations { get; set; } = [];
}

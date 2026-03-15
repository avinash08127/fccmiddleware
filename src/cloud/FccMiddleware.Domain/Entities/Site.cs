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

    /// <summary>
    /// Monotonically increasing counter incremented on any peer-directory-affecting event
    /// (agent registration, deactivation, role change, heartbeat expiry).
    /// Agents compare this to their cached value to detect peer directory staleness.
    /// </summary>
    public long PeerDirectoryVersion { get; set; }

    /// <summary>
    /// P2-15: The highest leader epoch the cloud has accepted from any agent at this site.
    /// Agent-side elections are authoritative; the cloud records the result.
    /// </summary>
    public long HaLeaderEpoch { get; set; }

    /// <summary>
    /// P2-15: The agent ID that claimed the current <see cref="HaLeaderEpoch"/>.
    /// Null until the first agent reports a leadership epoch.
    /// </summary>
    public Guid? HaLeaderAgentId { get; set; }

    // Navigation properties
    public LegalEntity LegalEntity { get; set; } = null!;
    public ICollection<Pump> Pumps { get; set; } = [];
    public ICollection<FccConfig> FccConfigs { get; set; } = [];
    public ICollection<AgentRegistration> AgentRegistrations { get; set; } = [];
}

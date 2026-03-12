namespace FccMiddleware.Contracts.MasterData;

/// <summary>
/// Request body for PUT /api/v1/master-data/sites.
/// </summary>
public sealed class SiteSyncRequest
{
    /// <summary>
    /// When true, records absent from this request are treated as stale and may be soft-deactivated.
    /// Default false keeps the sync batch as upsert-only.
    /// </summary>
    public bool IsFullSnapshot { get; init; }

    public List<SiteRecord> Sites { get; init; } = [];
}

/// <summary>
/// A single site record sent by Databricks.
/// </summary>
public sealed class SiteRecord
{
    public Guid Id { get; init; }
    public string SiteCode { get; init; } = null!;
    public Guid LegalEntityId { get; init; }
    public string SiteName { get; init; } = null!;

    /// <summary>Operating model: COCO, CODO, DODO, DOCO.</summary>
    public string OperatingModel { get; init; } = null!;

    /// <summary>Connectivity state: CONNECTED or DISCONNECTED.</summary>
    public string ConnectivityMode { get; init; } = null!;

    /// <summary>Company taxpayer ID used for fiscalized documents for the site.</summary>
    public string CompanyTaxPayerId { get; init; } = null!;

    /// <summary>Dealer/operator name for dealer-operated sites.</summary>
    public string? OperatorName { get; init; }

    /// <summary>Dealer/operator taxpayer ID for dealer-operated sites.</summary>
    public string? OperatorTaxPayerId { get; init; }

    /// <summary>Whether the site participates in pre-auth workflows and reconciliation.</summary>
    public bool? SiteUsesPreAuth { get; init; }

    /// <summary>Effective fiscalization mode for this site.</summary>
    public string FiscalizationMode { get; init; } = null!;

    /// <summary>Optional tax authority endpoint used by the site.</summary>
    public string? TaxAuthorityEndpoint { get; init; }

    public bool RequireCustomerTaxId { get; init; }

    public bool FiscalReceiptRequired { get; init; }

    /// <summary>Optional Odoo-side site identifier.</summary>
    public string? OdooSiteId { get; init; }

    /// <summary>Reconciliation amount tolerance as a percentage (e.g. 2.0 = 2%). Owned by Databricks.</summary>
    public decimal? AmountTolerancePercent { get; init; }

    /// <summary>Reconciliation amount tolerance in minor currency units. Owned by Databricks.</summary>
    public long? AmountToleranceAbsolute { get; init; }

    /// <summary>Reconciliation time window in minutes for pump/nozzle matching. Owned by Databricks.</summary>
    public int? TimeWindowMinutes { get; init; }

    public bool IsActive { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

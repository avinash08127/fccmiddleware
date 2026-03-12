namespace FccMiddleware.Contracts.MasterData;

/// <summary>
/// Request body for PUT /api/v1/master-data/legal-entities.
/// </summary>
public sealed class LegalEntitySyncRequest
{
    /// <summary>
    /// When true, records absent from this request are treated as stale and may be soft-deactivated.
    /// Default false keeps the sync batch as upsert-only.
    /// </summary>
    public bool IsFullSnapshot { get; init; }

    public List<LegalEntityRecord> LegalEntities { get; init; } = [];
}

/// <summary>
/// A single legal entity record sent by Databricks.
/// </summary>
public sealed class LegalEntityRecord
{
    /// <summary>Middleware-assigned UUID (stable across syncs).</summary>
    public Guid Id { get; init; }

    /// <summary>Unique business code for the legal entity (e.g., "MW", "TZ-001").</summary>
    public string Code { get; init; } = null!;

    /// <summary>Full legal entity name.</summary>
    public string Name { get; init; } = null!;

    /// <summary>ISO 3166-1 alpha-2/alpha-3 country code for the legal entity.</summary>
    public string CountryCode { get; init; } = null!;

    /// <summary>Human-readable country name.</summary>
    public string CountryName { get; init; } = null!;

    /// <summary>ISO 4217 currency code (e.g., MWK, TZS).</summary>
    public string CurrencyCode { get; init; } = null!;

    /// <summary>Tax authority code for the legal entity (e.g., ZIMRA, MRA).</summary>
    public string TaxAuthorityCode { get; init; } = null!;

    /// <summary>Default fiscalization mode inherited by sites unless their site sync overrides it.</summary>
    public string DefaultFiscalizationMode { get; init; } = null!;

    /// <summary>Optional external fiscalization provider/system code.</summary>
    public string? FiscalizationProvider { get; init; }

    /// <summary>Default IANA timezone for the legal entity.</summary>
    public string DefaultTimezone { get; init; } = null!;

    /// <summary>Required reference back to the Odoo company record.</summary>
    public string OdooCompanyId { get; init; } = null!;

    public bool IsActive { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

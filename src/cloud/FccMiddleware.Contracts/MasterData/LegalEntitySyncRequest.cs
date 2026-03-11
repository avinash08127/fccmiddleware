namespace FccMiddleware.Contracts.MasterData;

/// <summary>
/// Request body for PUT /api/v1/master-data/legal-entities.
/// </summary>
public sealed class LegalEntitySyncRequest
{
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

    /// <summary>ISO 4217 currency code (e.g., MWK, TZS).</summary>
    public string CurrencyCode { get; init; } = null!;

    /// <summary>Optional ISO 3166-1 alpha-2 country code.</summary>
    public string? Country { get; init; }

    public bool IsActive { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

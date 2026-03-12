namespace FccMiddleware.Contracts.MasterData;

/// <summary>
/// Request body for PUT /api/v1/master-data/products.
/// </summary>
public sealed class ProductSyncRequest
{
    /// <summary>
    /// When true, records absent from this request are treated as stale and may be soft-deactivated.
    /// Default false keeps the sync batch as upsert-only.
    /// </summary>
    public bool IsFullSnapshot { get; init; }

    public List<ProductRecord> Products { get; init; } = [];
}

/// <summary>
/// A single product record sent by Databricks.
/// Products are scoped to a legal entity — the same canonical code (e.g., PMS) may exist
/// with different IDs for different legal entities.
/// </summary>
public sealed class ProductRecord
{
    public Guid Id { get; init; }
    public Guid LegalEntityId { get; init; }

    /// <summary>Canonical fuel product code (e.g., PMS, AGO, IK, DPK).</summary>
    public string CanonicalCode { get; init; } = null!;

    /// <summary>Human-readable product name.</summary>
    public string DisplayName { get; init; } = null!;

    public bool IsActive { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

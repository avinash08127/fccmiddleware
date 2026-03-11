namespace FccMiddleware.Contracts.MasterData;

/// <summary>
/// Request body for PUT /api/v1/master-data/sites.
/// </summary>
public sealed class SiteSyncRequest
{
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

    /// <summary>Optional IANA timezone name (e.g., Africa/Lagos).</summary>
    public string? Timezone { get; init; }

    public bool IsActive { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// API key issued to Databricks for pushing master data via PUT /api/v1/master-data/*.
/// Global scope — not tied to a specific legal entity.
/// The raw key is never stored — only its SHA-256 hex hash.
/// </summary>
public class DatabricksApiKey
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 hex (lowercase) of the raw API key.</summary>
    public string KeyHash { get; set; } = null!;

    /// <summary>Human-readable label (e.g., "Databricks Production", "Databricks Staging").</summary>
    public string Label { get; set; } = null!;

    /// <summary>Role granted by this key. Must be "master-data-sync" for master data endpoints.</summary>
    public string Role { get; set; } = null!;

    /// <summary>Whether the key is currently enabled.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optional hard expiry. Null means the key never expires unless revoked.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Set when the key is explicitly revoked.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Returns true when the key should be accepted for authentication.
    /// </summary>
    public bool IsValid(DateTimeOffset asOf) =>
        IsActive
        && RevokedAt is null
        && (ExpiresAt is null || ExpiresAt > asOf);
}

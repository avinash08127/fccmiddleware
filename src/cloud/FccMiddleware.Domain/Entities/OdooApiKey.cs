namespace FccMiddleware.Domain.Entities;

/// <summary>
/// API key issued to Odoo for polling PENDING transactions via GET /api/v1/transactions.
/// Scoped to a single legal entity. The raw key is never stored — only its SHA-256 hex hash.
/// Plaintext is stored separately in AWS Secrets Manager for the Odoo service team.
/// </summary>
public class OdooApiKey
{
    public Guid Id { get; set; }

    /// <summary>Legal entity this key is scoped to. Only transactions for this tenant are returned.</summary>
    public Guid LegalEntityId { get; set; }

    /// <summary>SHA-256 hex (lowercase) of the raw API key. Looked up on each request.</summary>
    public string KeyHash { get; set; } = null!;

    /// <summary>Human-readable label (e.g., "Odoo Production MW", "Odoo Staging TZ").</summary>
    public string Label { get; set; } = null!;

    /// <summary>Whether the key is currently enabled. Set to false to soft-disable without revoking.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optional hard expiry. Null means the key never expires unless revoked.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Set when the key is explicitly revoked. Non-null means the key is invalid.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Returns true when the key should be accepted for authentication.
    /// A key must be active, not revoked, and not past its expiry date.
    /// </summary>
    public bool IsValid(DateTimeOffset asOf) =>
        IsActive
        && RevokedAt is null
        && (ExpiresAt is null || ExpiresAt > asOf);
}

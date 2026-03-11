namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Opaque refresh token for device JWT rotation.
/// Stored as SHA-256 hash — the plaintext is returned to the device exactly once.
/// Each refresh issues a new token and revokes the previous one (rotation).
/// </summary>
public class DeviceRefreshToken
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }

    /// <summary>SHA-256 hex (lowercase) of the raw refresh token.</summary>
    public string TokenHash { get; set; } = null!;

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Navigation
    public AgentRegistration Device { get; set; } = null!;

    /// <summary>
    /// Returns true if this refresh token is currently valid.
    /// </summary>
    public bool IsValid(DateTimeOffset asOf) =>
        RevokedAt is null && ExpiresAt > asOf;
}

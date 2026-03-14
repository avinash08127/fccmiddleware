using System.Text.Json.Serialization;
using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Contracts.AgentControl;

/// <summary>
/// Portal-facing history row for a single bootstrap token lifecycle.
/// The raw bootstrap token and its hash are never exposed after creation.
/// </summary>
public sealed class BootstrapTokenHistoryRow
{
    public Guid TokenId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = null!;

    /// <summary>
    /// Status physically stored on the bootstrap token row.
    /// Typically ACTIVE, USED, or REVOKED; EXPIRED is usually computed on read.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProvisioningTokenStatus StoredStatus { get; set; }

    /// <summary>
    /// Effective status returned to operators after evaluating expiry timestamps.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProvisioningTokenStatus EffectiveStatus { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByDeviceId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? CreatedByActorId { get; set; }
    public string? CreatedByActorDisplay { get; set; }
    public string? RevokedByActorId { get; set; }
    public string? RevokedByActorDisplay { get; set; }
}

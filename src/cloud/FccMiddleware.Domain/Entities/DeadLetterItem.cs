using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Operational dead-letter record surfaced to the portal for manual retry/discard flows.
/// </summary>
public class DeadLetterItem : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = null!;
    public DeadLetterType Type { get; set; } = DeadLetterType.UNKNOWN;
    public string? FccTransactionId { get; set; }
    public string? RawPayloadRef { get; set; }
    public string? RawPayloadJson { get; set; }
    public DeadLetterReason FailureReason { get; set; } = DeadLetterReason.UNKNOWN;
    public string ErrorCode { get; set; } = null!;
    public string ErrorMessage { get; set; } = null!;
    public DeadLetterStatus Status { get; set; } = DeadLetterStatus.PENDING;
    public int RetryCount { get; set; }
    public DateTimeOffset? LastRetryAt { get; set; }
    public string? RetryHistoryJson { get; set; }
    public string? DiscardReason { get; set; }
    public string? DiscardedBy { get; set; }
    public DateTimeOffset? DiscardedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

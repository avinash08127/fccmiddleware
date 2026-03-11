using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Durable record of a dispense-to-pre-auth reconciliation attempt.
/// One reconciliation record exists per transaction at a pre-auth-enabled site.
/// </summary>
public class ReconciliationRecord
{
    public Guid Id { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = null!;
    public Guid TransactionId { get; set; }
    public Guid? PreAuthId { get; set; }
    public string? OdooOrderId { get; set; }
    public int PumpNumber { get; set; }
    public int NozzleNumber { get; set; }
    public long? AuthorizedAmountMinorUnits { get; set; }
    public long ActualAmountMinorUnits { get; set; }
    public long? VarianceMinorUnits { get; set; }
    public long? AbsoluteVarianceMinorUnits { get; set; }
    public decimal? VariancePercent { get; set; }
    public bool? WithinTolerance { get; set; }
    public string MatchMethod { get; set; } = "NONE";
    public ReconciliationStatus Status { get; set; } = ReconciliationStatus.UNMATCHED;
    public bool AmbiguityFlag { get; set; }
    public string? AmbiguityReason { get; set; }
    public DateTimeOffset LastMatchAttemptAt { get; set; }
    public DateTimeOffset? MatchedAt { get; set; }
    public string? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public string? ReviewReason { get; set; }
    public DateTimeOffset? EscalatedAtUtc { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

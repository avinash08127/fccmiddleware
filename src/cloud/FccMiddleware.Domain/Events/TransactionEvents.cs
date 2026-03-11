namespace FccMiddleware.Domain.Events;

/// <summary>Raw transaction received and normalized.</summary>
public sealed class TransactionIngested : DomainEvent
{
    public override string EventType => "TransactionIngested";
    public Guid TransactionId { get; init; }
    public string FccTransactionId { get; init; } = null!;
    public string FccVendor { get; init; } = null!;
    public int PumpNumber { get; init; }
    public long TotalAmountMinorUnits { get; init; }
    public string CurrencyCode { get; init; } = null!;
}

/// <summary>Duplicate detected; original ID referenced.</summary>
public sealed class TransactionDeduplicated : DomainEvent
{
    public override string EventType => "TransactionDeduplicated";
    public string FccTransactionId { get; init; } = null!;
    public Guid ExistingTransactionId { get; init; }
    public string DedupKey { get; init; } = null!;
}

/// <summary>PENDING transaction flagged as stale (threshold exceeded, status unchanged).</summary>
public sealed class TransactionStaleFlagged : DomainEvent
{
    public override string EventType => "TransactionStaleFlagged";
    public Guid TransactionId { get; init; }
    public string FccTransactionId { get; init; } = null!;
    public int StalePendingThresholdDays { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
}

/// <summary>Odoo confirmed receipt.</summary>
public sealed class TransactionSyncedToOdoo : DomainEvent
{
    public override string EventType => "TransactionSyncedToOdoo";
    public Guid TransactionId { get; init; }
    public string OdooOrderId { get; init; } = null!;
    public DateTimeOffset AcknowledgedAt { get; init; }
}

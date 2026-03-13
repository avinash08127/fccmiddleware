using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Exceptions;
using FccMiddleware.Domain.Interfaces;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// A canonical fuel dispensing transaction. Partitioned by CreatedAt (monthly range).
/// The composite PK is (Id, CreatedAt) as required for PostgreSQL range partitioning.
/// Currency amounts are stored as minor units (cents). Volume in microlitres.
/// </summary>
public class Transaction : ITenantScoped
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }  // Partition key — part of composite PK
    public Guid LegalEntityId { get; set; }
    public string FccTransactionId { get; set; } = null!;
    public string SiteCode { get; set; } = null!;
    public int PumpNumber { get; set; }
    public int NozzleNumber { get; set; }
    public string ProductCode { get; set; } = null!;
    public long VolumeMicrolitres { get; set; }
    public long AmountMinorUnits { get; set; }
    public long UnitPriceMinorPerLitre { get; set; }
    public string CurrencyCode { get; set; } = null!;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string? FiscalReceiptNumber { get; set; }
    public string? FccCorrelationId { get; set; }
    public FccVendor FccVendor { get; set; }
    public string? AttendantId { get; set; }
    public TransactionStatus Status { get; set; } = TransactionStatus.PENDING;
    public IngestionSource IngestionSource { get; set; }
    public string? RawPayloadRef { get; set; }
    public string? OdooOrderId { get; set; }
    public DateTimeOffset? SyncedToOdooAt { get; set; }
    public Guid? PreAuthId { get; set; }
    public ReconciliationStatus? ReconciliationStatus { get; set; }
    public bool IsDuplicate { get; set; } = false;
    public Guid? DuplicateOfId { get; set; }
    public bool IsStale { get; set; } = false;
    public Guid CorrelationId { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Transitions the transaction to <paramref name="newStatus"/> according to the state machine
    /// defined in §5.1 of tier-1-2-state-machine-formal-definitions.md.
    /// Throws <see cref="InvalidTransactionTransitionException"/> for any invalid transition.
    /// </summary>
    public void Transition(TransactionStatus newStatus)
    {
        var allowed = (Status, newStatus) switch
        {
            (TransactionStatus.PENDING,      TransactionStatus.SYNCED_TO_ODOO) => true,
            (TransactionStatus.PENDING,      TransactionStatus.DUPLICATE)      => true,
            (TransactionStatus.PENDING,      TransactionStatus.ARCHIVED)       => true,
            (TransactionStatus.SYNCED_TO_ODOO, TransactionStatus.ARCHIVED)    => true,
            (TransactionStatus.DUPLICATE,    TransactionStatus.ARCHIVED)       => true,
            _ => false
        };

        if (!allowed)
            throw new InvalidTransactionTransitionException(Status, newStatus);

        Status = newStatus;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

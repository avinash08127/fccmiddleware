using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Exceptions;
using FccMiddleware.Domain.Common;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// A pre-authorization request from Odoo POS via an Edge Agent.
/// The idempotency key is (OdooOrderId, SiteCode).
/// Currency amounts are stored as minor units (cents). Volume in millilitres.
/// CustomerTaxId is sensitive — never log this field.
/// Matches pre-auth-record.schema.json.
/// </summary>
public class PreAuthRecord
{
    public Guid Id { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = null!;
    public string OdooOrderId { get; set; } = null!;
    public int PumpNumber { get; set; }
    public int NozzleNumber { get; set; }
    public string ProductCode { get; set; } = null!;
    public string CurrencyCode { get; set; } = null!;

    /// <summary>Pre-auth amount in minor units. Always > 0. Authorization is always by amount.</summary>
    public long RequestedAmountMinorUnits { get; set; }

    /// <summary>Price per litre at time of authorization, in minor units per litre. Informational only.</summary>
    public long UnitPriceMinorPerLitre { get; set; }

    public long? AuthorizedAmountMinorUnits { get; set; }
    public long? ActualAmountMinorUnits { get; set; }
    public long? ActualVolumeMillilitres { get; set; }

    /// <summary>actualAmount - requestedAmount in minor units. Negative = under-dispense. Populated at COMPLETED.</summary>
    public long? AmountVarianceMinorUnits { get; set; }

    /// <summary>ABS(amountVariance) / requestedAmount * 10000, rounded. Units: basis points. Populated at COMPLETED.</summary>
    public int? VarianceBps { get; set; }

    public PreAuthStatus Status { get; set; } = PreAuthStatus.PENDING;
    public string? FccCorrelationId { get; set; }
    public string? FccAuthorizationCode { get; set; }
    public string? FailureReason { get; set; }
    public string? VehicleNumber { get; set; }
    [Sensitive]
    public string? CustomerName { get; set; }

    /// <summary>Customer Tax Identification Number (TIN). PII — never log.</summary>
    [Sensitive]
    public string? CustomerTaxId { get; set; }

    public string? CustomerBusinessName { get; set; }
    public string? AttendantId { get; set; }

    /// <summary>
    /// fccTransactionId of the final dispense transaction matched by the reconciliation engine.
    /// Null until COMPLETED state.
    /// </summary>
    public string? MatchedFccTransactionId { get; set; }

    /// <summary>Middleware UUID of the matched Transaction record. Internal FK.</summary>
    public Guid? MatchedTransactionId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AuthorizedAt { get; set; }
    public DateTimeOffset? DispensingAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? ExpiredAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation properties
    public LegalEntity LegalEntity { get; set; } = null!;

    /// <summary>
    /// Transitions the pre-auth record to <paramref name="newStatus"/> according to the state machine
    /// defined in §5.2 of tier-1-2-state-machine-formal-definitions.md.
    /// Terminal states (no outgoing transitions): COMPLETED, CANCELLED, EXPIRED, FAILED.
    /// Throws <see cref="InvalidPreAuthTransitionException"/> for any invalid transition.
    /// </summary>
    public void Transition(PreAuthStatus newStatus)
    {
        var allowed = (Status, newStatus) switch
        {
            (PreAuthStatus.PENDING,    PreAuthStatus.AUTHORIZED)  => true,
            (PreAuthStatus.PENDING,    PreAuthStatus.CANCELLED)   => true,
            (PreAuthStatus.PENDING,    PreAuthStatus.EXPIRED)     => true,
            (PreAuthStatus.PENDING,    PreAuthStatus.FAILED)      => true,
            (PreAuthStatus.AUTHORIZED, PreAuthStatus.DISPENSING)  => true,
            (PreAuthStatus.AUTHORIZED, PreAuthStatus.COMPLETED)   => true,
            (PreAuthStatus.AUTHORIZED, PreAuthStatus.CANCELLED)   => true,
            (PreAuthStatus.AUTHORIZED, PreAuthStatus.EXPIRED)     => true,
            (PreAuthStatus.AUTHORIZED, PreAuthStatus.FAILED)      => true,
            (PreAuthStatus.DISPENSING, PreAuthStatus.COMPLETED)   => true,
            (PreAuthStatus.DISPENSING, PreAuthStatus.CANCELLED)   => true,
            (PreAuthStatus.DISPENSING, PreAuthStatus.EXPIRED)     => true,
            (PreAuthStatus.DISPENSING, PreAuthStatus.FAILED)      => true,
            _ => false
        };

        if (!allowed)
            throw new InvalidPreAuthTransitionException(Status, newStatus);

        Status = newStatus;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

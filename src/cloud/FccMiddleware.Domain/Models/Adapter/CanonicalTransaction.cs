using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Domain.Models.Adapter;

/// <summary>
/// Vendor-neutral representation of a fuel dispensing transaction produced by an IFccAdapter.
/// Contains only the fields an adapter can populate from raw vendor data.
/// The Application layer maps this to the Transaction entity and adds middleware-generated fields
/// (Id, LegalEntityId, Status, IngestionSource, IngestedAt, CorrelationId, etc.).
/// </summary>
public sealed record CanonicalTransaction
{
    /// <summary>Opaque transaction ID from the FCC. Forms the dedup key with SiteCode.</summary>
    public required string FccTransactionId { get; init; }

    /// <summary>Site where the dispense occurred. From the RawPayloadEnvelope.</summary>
    public required string SiteCode { get; init; }

    /// <summary>Physical pump number (after PumpNumberOffset applied from SiteFccConfig).</summary>
    public required int PumpNumber { get; init; }

    /// <summary>Physical nozzle number on the pump.</summary>
    public required int NozzleNumber { get; init; }

    /// <summary>Canonical product code after vendor product code mapping applied.</summary>
    public required string ProductCode { get; init; }

    /// <summary>Dispensed volume in microlitres (1 L = 1,000,000 µL).</summary>
    public required long VolumeMicrolitres { get; init; }

    /// <summary>Total transaction amount in minor currency units (e.g., cents, ngwe, kobo).</summary>
    public required long AmountMinorUnits { get; init; }

    /// <summary>Price per litre in minor currency units.</summary>
    public required long UnitPriceMinorPerLitre { get; init; }

    /// <summary>ISO 4217 currency code. From SiteFccConfig.</summary>
    public required string CurrencyCode { get; init; }

    /// <summary>Dispense start time in UTC.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Dispense completion time in UTC. Must be >= StartedAt.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>FCC vendor that produced this transaction. From the RawPayloadEnvelope.</summary>
    public required FccVendor FccVendor { get; init; }

    /// <summary>Fiscal receipt reference if the FCC fiscalizes directly. Null otherwise.</summary>
    public string? FiscalReceiptNumber { get; init; }

    /// <summary>Attendant/operator identifier from FCC. Null if not captured.</summary>
    public string? AttendantId { get; init; }
}

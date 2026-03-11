namespace FccMiddleware.Contracts.Ingestion;

/// <summary>
/// A single pre-normalized transaction record submitted by an Edge Agent in a batch upload.
/// Contains the canonical transaction fields the Edge Agent captured and buffered locally.
/// </summary>
public sealed record UploadTransactionRecord
{
    /// <summary>Opaque transaction ID from the FCC. Forms the dedup key with SiteCode.</summary>
    public required string FccTransactionId { get; init; }

    /// <summary>Site code where the dispense occurred. Must match the JWT 'site' claim.</summary>
    public required string SiteCode { get; init; }

    /// <summary>FCC vendor identifier (e.g., "DOMS", "RADIX").</summary>
    public required string FccVendor { get; init; }

    /// <summary>Physical pump number.</summary>
    public required int PumpNumber { get; init; }

    /// <summary>Physical nozzle number on the pump.</summary>
    public required int NozzleNumber { get; init; }

    /// <summary>Canonical product code.</summary>
    public required string ProductCode { get; init; }

    /// <summary>Dispensed volume in microlitres (1 L = 1,000,000 µL).</summary>
    public required long VolumeMicrolitres { get; init; }

    /// <summary>Total transaction amount in minor currency units (e.g., cents, kobo).</summary>
    public required long AmountMinorUnits { get; init; }

    /// <summary>Price per litre in minor currency units.</summary>
    public required long UnitPriceMinorPerLitre { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string CurrencyCode { get; init; }

    /// <summary>Dispense start time in UTC.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Dispense completion time in UTC.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Fiscal receipt reference if the FCC fiscalizes directly. Null otherwise.</summary>
    public string? FiscalReceiptNumber { get; init; }

    /// <summary>Attendant/operator identifier from FCC. Null if not captured.</summary>
    public string? AttendantId { get; init; }
}

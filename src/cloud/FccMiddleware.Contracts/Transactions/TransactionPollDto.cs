namespace FccMiddleware.Contracts.Transactions;

/// <summary>
/// Canonical transaction record returned by GET /api/v1/transactions (Odoo poll endpoint).
/// All money amounts are in minor currency units (e.g., cents). Volume is in microlitres.
/// </summary>
public sealed record TransactionPollDto
{
    /// <summary>Middleware-assigned UUID.</summary>
    public required Guid Id { get; init; }

    /// <summary>Opaque FCC transaction ID. Part of the dedup key with SiteCode.</summary>
    public required string FccTransactionId { get; init; }

    /// <summary>Site where the dispense occurred.</summary>
    public required string SiteCode { get; init; }

    /// <summary>Physical pump number.</summary>
    public required int PumpNumber { get; init; }

    /// <summary>Physical nozzle number.</summary>
    public required int NozzleNumber { get; init; }

    /// <summary>Canonical product code (e.g., PMS, AGO).</summary>
    public required string ProductCode { get; init; }

    /// <summary>Dispensed volume in microlitres (1 L = 1 000 000 µL).</summary>
    public required long VolumeMicrolitres { get; init; }

    /// <summary>Total transaction value in minor currency units (e.g., cents).</summary>
    public required long AmountMinorUnits { get; init; }

    /// <summary>Unit price per litre in minor currency units.</summary>
    public required long UnitPriceMinorPerLitre { get; init; }

    /// <summary>ISO 4217 currency code (e.g., MWK, TZS).</summary>
    public required string CurrencyCode { get; init; }

    /// <summary>Dispense start time (UTC).</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Dispense completion time (UTC).</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>UTC timestamp when the transaction was ingested by the middleware.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Transaction lifecycle status (always PENDING for poll results).</summary>
    public required string Status { get; init; }

    /// <summary>Correlation ID propagated from ingestion through to all downstream events.</summary>
    public required Guid CorrelationId { get; init; }

    /// <summary>Fiscal receipt reference number, if captured at the FCC.</summary>
    public string? FiscalReceiptNumber { get; init; }

    /// <summary>Attendant/operator ID from the FCC, if available.</summary>
    public string? AttendantId { get; init; }

    /// <summary>
    /// True when the transaction has not been acknowledged by Odoo within the configured
    /// stale-pending threshold. Odoo can still acknowledge stale transactions.
    /// </summary>
    public required bool IsStale { get; init; }

    /// <summary>FCC vendor that produced this transaction.</summary>
    public required string FccVendor { get; init; }

    /// <summary>How this transaction reached the cloud (FCC_PUSH, EDGE_UPLOAD, etc.).</summary>
    public required string IngestionSource { get; init; }
}

namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Fiscalization service for post-dispense fiscal receipt generation (ADV-7.1).
///
/// Used in Scenario A where the primary FCC (DOMS/Radix) controls pumps and provides
/// transaction data, and a secondary fiscal device (e.g. Advatec EFD) generates
/// TRA-compliant fiscal receipts after each dispense.
///
/// The flow is:
///   1. Primary FCC transaction is ingested and buffered by <see cref="Ingestion.IngestionOrchestrator"/>
///   2. This service POSTs customer/transaction data to the fiscal device
///   3. The fiscal device generates a fiscal receipt and returns it via webhook
///   4. The fiscal receipt data is attached to the original buffered transaction
///
/// Implementations must be thread-safe. The <see cref="Ingestion.IngestionOrchestrator"/>
/// serializes fiscalization calls with the poll lock, but implementations should still
/// guard internal state for robustness.
/// </summary>
public interface IFiscalizationService
{
    /// <summary>
    /// Submits a completed transaction for fiscal receipt generation.
    /// Posts customer data to the fiscal device and awaits the receipt webhook.
    /// </summary>
    /// <param name="transaction">The buffered canonical transaction to fiscalize.</param>
    /// <param name="context">Customer and payment context for the fiscal receipt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing the fiscal receipt code and TRA verification URL on success.</returns>
    Task<FiscalizationResult> SubmitForFiscalizationAsync(
        CanonicalTransaction transaction,
        FiscalizationContext context,
        CancellationToken ct);

    /// <summary>
    /// Checks if the fiscal device is reachable (TCP connect or health endpoint).
    /// Never throws — returns false on any failure.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);
}

/// <summary>
/// Customer and payment context required for fiscal receipt generation.
/// Populated from the pre-auth record or site-level defaults.
/// </summary>
public sealed record FiscalizationContext(
    /// <summary>Customer TIN for TRA fiscal receipts. PII — never log.</summary>
    string? CustomerTaxId,
    /// <summary>Customer display name on the fiscal receipt.</summary>
    string? CustomerName,
    /// <summary>TRA CustIdType (1=TIN, 2=DL, 3=Voters, 4=Passport, 5=NID, 6=NIL).</summary>
    int? CustomerIdType,
    /// <summary>Payment method for the fiscal receipt (CASH, CCARD, EMONEY, INVOICE, CHEQUE).</summary>
    string? PaymentType);

/// <summary>
/// Result of a fiscalization attempt.
/// </summary>
public sealed record FiscalizationResult(
    /// <summary>Whether the fiscal receipt was successfully generated.</summary>
    bool Success,
    /// <summary>TRA fiscal receipt code (e.g. "9a8b7c6d5e1"). Null on failure.</summary>
    string? ReceiptCode = null,
    /// <summary>TRA verification URL (e.g. "https://virtual.tra.go.tz/efdmsrctverify/..."). Null on failure.</summary>
    string? ReceiptVCodeUrl = null,
    /// <summary>Total tax amount from the fiscal receipt. Null on failure.</summary>
    decimal? TotalTaxAmount = null,
    /// <summary>Error description when <see cref="Success"/> is false.</summary>
    string? ErrorMessage = null);

namespace FccMiddleware.Application.Transactions;

/// <summary>Result of a <see cref="PollTransactionsQuery"/>.</summary>
public sealed record PollTransactionsResult
{
    public required IReadOnlyList<PollTransactionRecord> Transactions { get; init; }
    public required bool HasMore { get; init; }

    /// <summary>
    /// Opaque cursor to pass as <c>cursor</c> on the next request.
    /// Null when <see cref="HasMore"/> is false.
    /// </summary>
    public string? NextCursor { get; init; }

    /// <summary>Total matching records. Null when omitted for performance reasons.</summary>
    public int? TotalCount { get; init; }
}

/// <summary>
/// Single transaction record returned by the Odoo poll query.
/// All money amounts are in minor units; volume is in microlitres.
/// </summary>
public sealed record PollTransactionRecord
{
    public required Guid Id { get; init; }
    public required string FccTransactionId { get; init; }
    public required string SiteCode { get; init; }
    public required int PumpNumber { get; init; }
    public required int NozzleNumber { get; init; }
    public required string ProductCode { get; init; }
    public required long VolumeMicrolitres { get; init; }
    public required long AmountMinorUnits { get; init; }
    public required long UnitPriceMinorPerLitre { get; init; }
    public required string CurrencyCode { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Status { get; init; }
    public required Guid CorrelationId { get; init; }
    public string? FiscalReceiptNumber { get; init; }
    public string? AttendantId { get; init; }
    public required bool IsStale { get; init; }
    public required string FccVendor { get; init; }
    public required string IngestionSource { get; init; }
}

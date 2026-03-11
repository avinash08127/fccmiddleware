namespace FccMiddleware.Domain.Models.Adapter;

/// <summary>
/// Result of IFccAdapter.FetchTransactionsAsync.
/// May contain zero transactions with HasMore=false; this is a valid no-data poll result.
/// </summary>
public sealed record TransactionBatch
{
    /// <summary>Normalized canonical transactions. May be empty.</summary>
    public required IReadOnlyList<CanonicalTransaction> Transactions { get; init; }

    /// <summary>
    /// Returned when the vendor supports token-based cursor progression.
    /// Pass back as FetchCursor.CursorToken on the next call.
    /// </summary>
    public string? NextCursorToken { get; init; }

    /// <summary>
    /// Returned when cursor progression is time-based rather than token-based.
    /// Use as the lower bound for the next FetchCursor.SinceUtc.
    /// </summary>
    public DateTimeOffset? HighWatermarkUtc { get; init; }

    /// <summary>
    /// True when an immediate follow-up fetch should continue. False means the
    /// caller should wait for the next scheduled poll interval.
    /// </summary>
    public required bool HasMore { get; init; }

    /// <summary>Vendor batch/message identifier for diagnostics. Null if not provided.</summary>
    public string? SourceBatchId { get; init; }
}

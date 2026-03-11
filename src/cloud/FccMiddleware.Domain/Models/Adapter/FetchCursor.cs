namespace FccMiddleware.Domain.Models.Adapter;

/// <summary>
/// Cursor input for IFccAdapter.FetchTransactionsAsync.
/// Either CursorToken or SinceUtc should be supplied; CursorToken takes precedence.
/// When neither is supplied the adapter should return the earliest available transactions.
/// </summary>
public sealed record FetchCursor
{
    /// <summary>
    /// Vendor-opaque continuation token returned from the previous TransactionBatch.
    /// When present, takes precedence over SinceUtc.
    /// </summary>
    public string? CursorToken { get; init; }

    /// <summary>
    /// Inclusive UTC lower bound used when a vendor token is unavailable.
    /// </summary>
    public DateTimeOffset? SinceUtc { get; init; }

    /// <summary>
    /// Caller hint for page size. The adapter may reduce but must not exceed
    /// the configured max page size.
    /// </summary>
    public int? Limit { get; init; }
}

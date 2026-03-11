namespace FccMiddleware.Contracts.Transactions;

/// <summary>
/// Response body for GET /api/v1/transactions (Odoo poll endpoint).
/// Contains a page of PENDING transactions plus cursor-based pagination metadata.
/// </summary>
public sealed record PollTransactionsResponse
{
    /// <summary>Transactions in this page, ordered oldest-first by ingest time.</summary>
    public required IReadOnlyList<TransactionPollDto> Data { get; init; }

    /// <summary>Pagination metadata.</summary>
    public required PollPageMeta Meta { get; init; }
}

/// <summary>
/// Pagination metadata included in every paged response.
/// </summary>
public sealed record PollPageMeta
{
    /// <summary>Number of records in this page.</summary>
    public required int PageSize { get; init; }

    /// <summary>Whether more records exist beyond this page.</summary>
    public required bool HasMore { get; init; }

    /// <summary>
    /// Opaque cursor to pass as <c>cursor</c> for the next request.
    /// Null when <see cref="HasMore"/> is false.
    /// </summary>
    public string? NextCursor { get; init; }

    /// <summary>
    /// Total matching PENDING records for this legal entity and filter set.
    /// May be null for performance reasons on large result sets.
    /// </summary>
    public int? TotalCount { get; init; }
}

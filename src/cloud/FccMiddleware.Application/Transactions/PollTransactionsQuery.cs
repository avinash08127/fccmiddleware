using MediatR;

namespace FccMiddleware.Application.Transactions;

/// <summary>
/// Fetches a paginated page of PENDING transactions for a given legal entity.
/// Used by the Odoo poll endpoint (GET /api/v1/transactions).
/// </summary>
public sealed record PollTransactionsQuery : IRequest<PollTransactionsResult>
{
    /// <summary>Legal entity scope — derived from the authenticated Odoo API key.</summary>
    public required Guid LegalEntityId { get; init; }

    /// <summary>Optional: filter to a single site.</summary>
    public string? SiteCode { get; init; }

    /// <summary>Optional: filter to a specific pump.</summary>
    public int? PumpNumber { get; init; }

    /// <summary>Optional: lower bound on CreatedAt (inclusive). Maps to 'from' / 'since' query param.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>
    /// Opaque pagination cursor from a previous response's <c>NextCursor</c>.
    /// Null or empty means start from the beginning.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>Page size. Clamped to [1, 100] by the handler. Default 50.</summary>
    public int PageSize { get; init; } = 50;
}

using System.Text;
using MediatR;

namespace FccMiddleware.Application.Transactions;

/// <summary>
/// Handles <see cref="PollTransactionsQuery"/> — returns a cursor-paginated page of PENDING
/// transactions for the requested legal entity, ordered oldest-first by (CreatedAt, Id).
///
/// Cursor encoding: Base64URL of "{ISO8601 createdAt}|{id GUID}"
/// The Infrastructure implementation targets ix_transactions_odoo_poll for query efficiency.
/// </summary>
public sealed class PollTransactionsHandler : IRequestHandler<PollTransactionsQuery, PollTransactionsResult>
{
    private readonly IPollTransactionsDbContext _db;

    public PollTransactionsHandler(IPollTransactionsDbContext db)
    {
        _db = db;
    }

    public async Task<PollTransactionsResult> Handle(
        PollTransactionsQuery request,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        // Decode the opaque cursor into (createdAt, id) for keyset pagination.
        DateTimeOffset? cursorCreatedAt = null;
        Guid? cursorId = null;
        if (!string.IsNullOrEmpty(request.Cursor)
            && TryDecodeCursor(request.Cursor, out var parsedCreatedAt, out var parsedId))
        {
            cursorCreatedAt = parsedCreatedAt;
            cursorId        = parsedId;
        }

        // Fetch pageSize + 1 to detect whether another page follows.
        var rows = await _db.FetchPendingPageAsync(
            legalEntityId:   request.LegalEntityId,
            siteCode:        request.SiteCode,
            pumpNumber:      request.PumpNumber,
            from:            request.From,
            cursorCreatedAt: cursorCreatedAt,
            cursorId:        cursorId,
            take:            pageSize + 1,
            ct:              cancellationToken);

        var hasMore = rows.Count > pageSize;
        var page    = hasMore ? rows[..pageSize] : rows;

        string? nextCursor = null;
        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            nextCursor = EncodeCursor(last.CreatedAt, last.Id);
        }

        return new PollTransactionsResult
        {
            Transactions = page,
            HasMore      = hasMore,
            NextCursor   = nextCursor,
            TotalCount   = null   // omitted for performance on large result sets
        };
    }

    // ── Cursor helpers (internal for unit testing) ────────────────────────────

    internal static string EncodeCursor(DateTimeOffset createdAt, Guid id)
    {
        var raw = $"{createdAt:O}|{id}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static bool TryDecodeCursor(string cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id        = default;

        try
        {
            // Reverse Base64URL to standard Base64.
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };

            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var sep = raw.IndexOf('|');
            if (sep < 0) return false;

            return DateTimeOffset.TryParse(raw[..sep], null,
                       System.Globalization.DateTimeStyles.RoundtripKind, out createdAt)
                && Guid.TryParse(raw[(sep + 1)..], out id);
        }
        catch
        {
            return false;
        }
    }
}

using System.Text;
using FccMiddleware.Domain.Enums;
using MediatR;

namespace FccMiddleware.Application.Reconciliation;

public sealed class GetReconciliationExceptionsHandler
    : IRequestHandler<GetReconciliationExceptionsQuery, GetReconciliationExceptionsResult>
{
    private static readonly ReconciliationStatus[] DefaultStatuses =
    [
        ReconciliationStatus.VARIANCE_FLAGGED,
        ReconciliationStatus.UNMATCHED
    ];

    private readonly IReconciliationDbContext _db;

    public GetReconciliationExceptionsHandler(IReconciliationDbContext db)
    {
        _db = db;
    }

    public async Task<GetReconciliationExceptionsResult> Handle(
        GetReconciliationExceptionsQuery request,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var lowerBound = request.From ?? request.Since;

        DateTimeOffset? cursorCreatedAt = null;
        Guid? cursorId = null;
        if (!string.IsNullOrWhiteSpace(request.Cursor)
            && TryDecodeCursor(request.Cursor, out var parsedCreatedAt, out var parsedId))
        {
            cursorCreatedAt = parsedCreatedAt;
            cursorId = parsedId;
        }

        var statuses = request.Status.HasValue
            ? [request.Status.Value]
            : DefaultStatuses;

        var rows = await _db.FetchExceptionsPageAsync(
            request.LegalEntityId,
            request.ScopedLegalEntityIds,
            request.AllowAllLegalEntities,
            request.SiteCode,
            statuses,
            lowerBound,
            request.To,
            cursorCreatedAt,
            cursorId,
            pageSize + 1,
            cancellationToken);

        var hasMore = rows.Count > pageSize;
        var page = hasMore ? rows[..pageSize] : rows;

        string? nextCursor = null;
        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            nextCursor = EncodeCursor(last.CreatedAt, last.ReconciliationId);
        }

        return new GetReconciliationExceptionsResult
        {
            Records = page,
            HasMore = hasMore,
            NextCursor = nextCursor
        };
    }

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
        id = default;

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };

            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var separatorIndex = raw.IndexOf('|');
            if (separatorIndex < 0)
            {
                return false;
            }

            return DateTimeOffset.TryParse(
                       raw[..separatorIndex],
                       null,
                       System.Globalization.DateTimeStyles.RoundtripKind,
                       out createdAt)
                   && Guid.TryParse(raw[(separatorIndex + 1)..], out id);
        }
        catch
        {
            return false;
        }
    }
}

using MediatR;

namespace FccMiddleware.Application.Transactions;

/// <summary>
/// Handles <see cref="GetSyncedTransactionIdsQuery"/> by returning FCC transaction IDs acknowledged
/// by Odoo for the requesting device's site since the supplied timestamp.
/// </summary>
public sealed class GetSyncedTransactionIdsHandler
    : IRequestHandler<GetSyncedTransactionIdsQuery, GetSyncedTransactionIdsResult>
{
    private readonly IPollTransactionsDbContext _db;

    public GetSyncedTransactionIdsHandler(IPollTransactionsDbContext db)
    {
        _db = db;
    }

    public async Task<GetSyncedTransactionIdsResult> Handle(
        GetSyncedTransactionIdsQuery request,
        CancellationToken cancellationToken)
    {
        var ids = await _db.FetchSyncedTransactionIdsAsync(
            request.LegalEntityId,
            request.SiteCode,
            request.Since,
            cancellationToken);

        return new GetSyncedTransactionIdsResult
        {
            FccTransactionIds = ids
        };
    }
}

using MediatR;

namespace FccMiddleware.Application.Transactions;

/// <summary>
/// Fetches FCC transaction IDs that reached SYNCED_TO_ODOO for a device's site since a given time.
/// Used by the Edge Agent status poll endpoint (GET /api/v1/transactions/synced-status).
/// </summary>
public sealed record GetSyncedTransactionIdsQuery : IRequest<GetSyncedTransactionIdsResult>
{
    /// <summary>Legal entity scope derived from the device JWT.</summary>
    public required Guid LegalEntityId { get; init; }

    /// <summary>Site code derived from the device JWT site claim.</summary>
    public required string SiteCode { get; init; }

    /// <summary>Inclusive lower bound on SyncedToOdooAt.</summary>
    public required DateTimeOffset Since { get; init; }
}

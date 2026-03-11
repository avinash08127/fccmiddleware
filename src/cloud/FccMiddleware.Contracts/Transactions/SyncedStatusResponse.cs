namespace FccMiddleware.Contracts.Transactions;

/// <summary>
/// Response body for GET /api/v1/transactions/synced-status.
/// Contains FCC transaction IDs that have reached SYNCED_TO_ODOO since the requested timestamp.
/// </summary>
public sealed record SyncedStatusResponse
{
    public required IReadOnlyList<string> FccTransactionIds { get; init; }
}

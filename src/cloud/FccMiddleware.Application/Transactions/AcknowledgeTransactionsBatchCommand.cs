using MediatR;

namespace FccMiddleware.Application.Transactions;

/// <summary>
/// Batch command to acknowledge one or more transactions as Odoo-processed.
/// Sent by the TransactionsController when Odoo calls POST /api/v1/transactions/acknowledge.
/// </summary>
public sealed record AcknowledgeTransactionsBatchCommand : IRequest<AcknowledgeTransactionsBatchResult>
{
    /// <summary>Tenant scope — derived from the authenticated Odoo API key's 'lei' claim.</summary>
    public required Guid LegalEntityId { get; init; }

    /// <summary>Items to acknowledge (1–500).</summary>
    public required IReadOnlyList<AcknowledgeTransactionItem> Items { get; init; }
}

/// <summary>A single item in the acknowledge batch.</summary>
public sealed record AcknowledgeTransactionItem
{
    public required Guid TransactionId { get; init; }
    public required string OdooOrderId { get; init; }
}

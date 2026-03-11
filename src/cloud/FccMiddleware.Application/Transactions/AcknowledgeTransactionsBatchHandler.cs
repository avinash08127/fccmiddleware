using System.Text.Json;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.Transactions;

/// <summary>
/// Handles AcknowledgeTransactionsBatchCommand.
///
/// For each item:
///   - NOT_FOUND:           no transaction with that ID in this tenant
///   - ALREADY_ACKNOWLEDGED: status=SYNCED_TO_ODOO and same odooOrderId (idempotent)
///   - CONFLICT:            status=SYNCED_TO_ODOO but different odooOrderId
///   - FAILED:              status is DUPLICATE, ARCHIVED, or any other non-PENDING state
///   - ACKNOWLEDGED:        status=PENDING → transitions to SYNCED_TO_ODOO; publishes outbox event
///
/// All mutations (transitions + outbox inserts) are saved in a single SaveChanges call.
/// </summary>
public sealed class AcknowledgeTransactionsBatchHandler
    : IRequestHandler<AcknowledgeTransactionsBatchCommand, AcknowledgeTransactionsBatchResult>
{
    private readonly IAcknowledgeTransactionsDbContext _db;
    private readonly ILogger<AcknowledgeTransactionsBatchHandler> _logger;

    public AcknowledgeTransactionsBatchHandler(
        IAcknowledgeTransactionsDbContext db,
        ILogger<AcknowledgeTransactionsBatchHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AcknowledgeTransactionsBatchResult> Handle(
        AcknowledgeTransactionsBatchCommand command,
        CancellationToken cancellationToken)
    {
        // Fetch all referenced transactions in a single query for efficiency (up to 500 IDs).
        var ids = command.Items.Select(i => i.TransactionId).ToList();
        var transactions = await _db.FindTransactionsByIdsAsync(ids, command.LegalEntityId, cancellationToken);
        var byId = transactions.ToDictionary(t => t.Id);

        var results = new List<SingleAcknowledgeResult>(command.Items.Count);
        var acknowledgedAt = DateTimeOffset.UtcNow;

        foreach (var item in command.Items)
        {
            if (!byId.TryGetValue(item.TransactionId, out var transaction))
            {
                results.Add(new SingleAcknowledgeResult
                {
                    TransactionId = item.TransactionId,
                    Outcome       = AcknowledgeOutcome.NOT_FOUND
                });
                continue;
            }

            if (transaction.Status == TransactionStatus.SYNCED_TO_ODOO)
            {
                using var syncedScope = _logger.BeginScope(new Dictionary<string, object?>
                {
                    ["correlationId"] = transaction.CorrelationId,
                    ["transactionId"] = transaction.Id
                });

                if (transaction.OdooOrderId == item.OdooOrderId)
                {
                    // Idempotent re-acknowledgement with the same order ID.
                    results.Add(new SingleAcknowledgeResult
                    {
                        TransactionId = item.TransactionId,
                        Outcome       = AcknowledgeOutcome.ALREADY_ACKNOWLEDGED
                    });
                }
                else
                {
                    // Conflict: already stamped with a different odooOrderId.
                    _logger.LogWarning(
                        "Acknowledge conflict for transaction {TransactionId}: already synced with order {ExistingOrderId}, requested {NewOrderId}",
                        transaction.Id, transaction.OdooOrderId, item.OdooOrderId);

                    results.Add(new SingleAcknowledgeResult
                    {
                        TransactionId = item.TransactionId,
                        Outcome       = AcknowledgeOutcome.CONFLICT,
                        ErrorCode     = "ACKNOWLEDGE.CONFLICT",
                        ErrorMessage  = $"Transaction is already acknowledged with order '{transaction.OdooOrderId}'."
                    });
                }
                continue;
            }

            if (transaction.Status != TransactionStatus.PENDING)
            {
                using var invalidStateScope = _logger.BeginScope(new Dictionary<string, object?>
                {
                    ["correlationId"] = transaction.CorrelationId,
                    ["transactionId"] = transaction.Id
                });

                // DUPLICATE, ARCHIVED, or any future terminal state — cannot acknowledge.
                _logger.LogWarning(
                    "Cannot acknowledge transaction {TransactionId} in status {Status}",
                    transaction.Id, transaction.Status);

                results.Add(new SingleAcknowledgeResult
                {
                    TransactionId = item.TransactionId,
                    Outcome       = AcknowledgeOutcome.FAILED,
                    ErrorCode     = "ACKNOWLEDGE.INVALID_STATUS",
                    ErrorMessage  = $"Transaction status '{transaction.Status}' cannot be acknowledged."
                });
                continue;
            }

            // ── Happy path: PENDING → SYNCED_TO_ODOO ─────────────────────────
            transaction.Transition(TransactionStatus.SYNCED_TO_ODOO);
            transaction.OdooOrderId   = item.OdooOrderId;
            transaction.SyncedToOdooAt = acknowledgedAt;

            _db.AddOutboxMessage(BuildSyncedOutboxMessage(transaction, acknowledgedAt));

            using (var acknowledgedScope = _logger.BeginScope(new Dictionary<string, object?>
                   {
                       ["correlationId"] = transaction.CorrelationId,
                       ["transactionId"] = transaction.Id
                   }))
            {
                _logger.LogInformation(
                    "Transaction {TransactionId} acknowledged with order {OdooOrderId} for tenant {LegalEntityId}",
                    transaction.Id, item.OdooOrderId, command.LegalEntityId);
            }

            results.Add(new SingleAcknowledgeResult
            {
                TransactionId = item.TransactionId,
                Outcome       = AcknowledgeOutcome.ACKNOWLEDGED
            });
        }

        // Single atomic SaveChanges for all mutations.
        await _db.SaveChangesAsync(cancellationToken);

        return new AcknowledgeTransactionsBatchResult
        {
            Results        = results,
            SucceededCount = results.Count(r => r.Outcome is AcknowledgeOutcome.ACKNOWLEDGED
                                                           or AcknowledgeOutcome.ALREADY_ACKNOWLEDGED),
            FailedCount    = results.Count(r => r.Outcome is AcknowledgeOutcome.NOT_FOUND
                                                          or AcknowledgeOutcome.CONFLICT
                                                          or AcknowledgeOutcome.FAILED)
        };
    }

    private static OutboxMessage BuildSyncedOutboxMessage(Transaction tx, DateTimeOffset acknowledgedAt) =>
        new()
        {
            EventType     = "TransactionSyncedToOdoo",
            Payload       = JsonSerializer.Serialize(new
            {
                transactionId  = tx.Id,
                odooOrderId    = tx.OdooOrderId,
                acknowledgedAt = acknowledgedAt,
                siteCode       = tx.SiteCode,
                legalEntityId  = tx.LegalEntityId
            }),
            CorrelationId = tx.CorrelationId,
            CreatedAt     = acknowledgedAt
        };
}

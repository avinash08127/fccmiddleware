using FccDesktopAgent.Core.Buffer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Buffer;

/// <summary>
/// T-DSK-014: Service-layer implementation for transaction updates.
/// Encapsulates the DB mutation logic previously inlined in OdooWsMessageHandler.
/// Scoped lifetime — depends on <see cref="AgentDbContext"/>.
/// </summary>
public sealed class TransactionUpdateService : ITransactionUpdateService
{
    private readonly AgentDbContext _db;
    private readonly ILogger<TransactionUpdateService> _logger;

    public TransactionUpdateService(AgentDbContext db, ILogger<TransactionUpdateService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<BufferedTransaction?> ApplyManagerUpdateAsync(
        string fccTransactionId, TransactionUpdateFields fields, CancellationToken ct)
    {
        var tx = await _db.Transactions
            .FirstOrDefaultAsync(x => x.FccTransactionId == fccTransactionId, ct);

        if (tx is null)
        {
            _logger.LogDebug("Manager update: transaction {FccId} not found", fccTransactionId);
            return null;
        }

        if (fields.OrderUuid is not null) tx.OrderUuid = fields.OrderUuid;
        if (fields.OdooOrderId is not null) tx.OdooOrderId = fields.OdooOrderId;
        if (fields.PaymentId is not null) tx.PaymentId = fields.PaymentId;
        if (fields.AddToCart.HasValue) tx.AddToCart = fields.AddToCart.Value;

        tx.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return tx;
    }

    public async Task<(BufferedTransaction? Transaction, bool ShouldBroadcast)> ApplyAttendantUpdateAsync(
        string fccTransactionId, TransactionUpdateFields fields, CancellationToken ct)
    {
        var tx = await _db.Transactions
            .FirstOrDefaultAsync(x => x.FccTransactionId == fccTransactionId, ct);

        if (tx is null)
        {
            _logger.LogDebug("Attendant update: transaction {FccId} not found", fccTransactionId);
            return (null, false);
        }

        bool shouldBroadcast = false;

        if (fields.AddToCart.HasValue)
        {
            tx.AddToCart = fields.AddToCart.Value;
            if (fields.PaymentId is not null)
                tx.PaymentId = fields.PaymentId;
            shouldBroadcast = true;
        }

        if (!string.IsNullOrEmpty(fields.OrderUuid))
        {
            tx.OrderUuid = fields.OrderUuid;
            if (fields.OdooOrderId is not null)
                tx.OdooOrderId = fields.OdooOrderId;
            if (fields.PaymentId is not null)
                tx.PaymentId = fields.PaymentId;
            shouldBroadcast = true;
        }

        tx.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return (tx, shouldBroadcast);
    }

    public async Task<bool> DiscardTransactionAsync(string fccTransactionId, CancellationToken ct)
    {
        var tx = await _db.Transactions
            .FirstOrDefaultAsync(x => x.FccTransactionId == fccTransactionId, ct);

        if (tx is null)
        {
            _logger.LogDebug("Discard: transaction {FccId} not found", fccTransactionId);
            return false;
        }

        tx.IsDiscard = true;
        tx.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return true;
    }
}

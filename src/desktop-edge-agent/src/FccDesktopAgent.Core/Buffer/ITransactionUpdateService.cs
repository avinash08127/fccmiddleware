using FccDesktopAgent.Core.Buffer.Entities;

namespace FccDesktopAgent.Core.Buffer;

/// <summary>
/// T-DSK-014: Encapsulates transaction mutation logic so that both the WebSocket
/// handler and the REST API route through the same authorization/validation layer
/// instead of modifying <see cref="BufferedTransaction"/> records directly.
/// </summary>
public interface ITransactionUpdateService
{
    /// <summary>
    /// Applies a manager-initiated update (order linkage, payment, add-to-cart).
    /// Returns the updated transaction, or null if the transaction was not found.
    /// </summary>
    Task<BufferedTransaction?> ApplyManagerUpdateAsync(
        string fccTransactionId, TransactionUpdateFields fields, CancellationToken ct);

    /// <summary>
    /// Applies an attendant-initiated update (add-to-cart and/or order linkage).
    /// Returns the updated transaction and whether a broadcast is warranted.
    /// </summary>
    Task<(BufferedTransaction? Transaction, bool ShouldBroadcast)> ApplyAttendantUpdateAsync(
        string fccTransactionId, TransactionUpdateFields fields, CancellationToken ct);

    /// <summary>
    /// Marks a transaction as discarded (manual manager approval).
    /// Returns true if the transaction was found and updated.
    /// </summary>
    Task<bool> DiscardTransactionAsync(string fccTransactionId, CancellationToken ct);
}

/// <summary>
/// Fields that can be updated on a <see cref="BufferedTransaction"/> via WebSocket or REST.
/// All properties are optional — only non-null values are applied.
/// </summary>
public sealed record TransactionUpdateFields
{
    public string? OrderUuid { get; init; }
    public string? OdooOrderId { get; init; }
    public string? PaymentId { get; init; }
    public bool? AddToCart { get; init; }
}

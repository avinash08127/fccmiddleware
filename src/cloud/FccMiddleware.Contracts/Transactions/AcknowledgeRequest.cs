using System.ComponentModel.DataAnnotations;

namespace FccMiddleware.Contracts.Transactions;

/// <summary>
/// Request body for POST /api/v1/transactions/acknowledge.
/// Odoo calls this after creating POS orders to stamp each transaction with its odooOrderId.
/// </summary>
public sealed record AcknowledgeRequest
{
    /// <summary>Between 1 and 500 acknowledgement items.</summary>
    [Required]
    [MinLength(1)]
    [MaxLength(500)]
    public required IReadOnlyList<AcknowledgeItem> Acknowledgements { get; init; }
}

/// <summary>A single acknowledgement: middleware transaction ID + Odoo order reference.</summary>
public sealed record AcknowledgeItem
{
    /// <summary>Middleware UUID of the transaction (the <c>id</c> field from the poll response).</summary>
    [Required]
    public required Guid Id { get; init; }

    /// <summary>Odoo POS order reference, e.g. "POS/2026/00042". Max 100 chars.</summary>
    [Required]
    [MinLength(1)]
    [MaxLength(100)]
    public required string OdooOrderId { get; init; }
}

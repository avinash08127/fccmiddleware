namespace FccMiddleware.Contracts.Transactions;

/// <summary>
/// Response body for POST /api/v1/transactions/acknowledge.
/// Contains a per-record outcome for every item in the request.
/// </summary>
public sealed record AcknowledgeResponse
{
    public required IReadOnlyList<AcknowledgeResult> Results { get; init; }

    /// <summary>Count of ACKNOWLEDGED + ALREADY_ACKNOWLEDGED outcomes.</summary>
    public required int SucceededCount { get; init; }

    /// <summary>Count of NOT_FOUND + CONFLICT + FAILED outcomes.</summary>
    public required int FailedCount { get; init; }
}

/// <summary>Per-record result for a single acknowledgement item.</summary>
public sealed record AcknowledgeResult
{
    /// <summary>The middleware transaction UUID from the request.</summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Outcome code:
    /// <list type="bullet">
    ///   <item><term>ACKNOWLEDGED</term><description>Successfully transitioned PENDING → SYNCED_TO_ODOO.</description></item>
    ///   <item><term>ALREADY_ACKNOWLEDGED</term><description>Already SYNCED_TO_ODOO with the same odooOrderId (idempotent).</description></item>
    ///   <item><term>CONFLICT</term><description>Already SYNCED_TO_ODOO with a different odooOrderId.</description></item>
    ///   <item><term>NOT_FOUND</term><description>No transaction with this ID exists for this tenant.</description></item>
    ///   <item><term>FAILED</term><description>Transaction is in a status that cannot be acknowledged (e.g. DUPLICATE, ARCHIVED).</description></item>
    /// </list>
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>Populated when Outcome is CONFLICT or FAILED.</summary>
    public AcknowledgeError? Error { get; init; }
}

/// <summary>Error detail for non-success outcomes.</summary>
public sealed record AcknowledgeError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

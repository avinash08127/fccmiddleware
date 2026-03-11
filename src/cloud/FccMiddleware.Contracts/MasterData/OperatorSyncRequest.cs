namespace FccMiddleware.Contracts.MasterData;

/// <summary>
/// Request body for PUT /api/v1/master-data/operators.
/// </summary>
public sealed class OperatorSyncRequest
{
    public List<OperatorRecord> Operators { get; init; } = [];
}

/// <summary>
/// A single operator record sent by Databricks.
/// </summary>
public sealed class OperatorRecord
{
    public Guid Id { get; init; }
    public Guid LegalEntityId { get; init; }

    /// <summary>Operator display name.</summary>
    public string Name { get; init; } = null!;

    /// <summary>Optional tax payer / TIN identifier.</summary>
    public string? TaxPayerId { get; init; }

    public bool IsActive { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

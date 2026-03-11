namespace FccMiddleware.Domain.Entities;

/// <summary>
/// A dealer or operator entity within a legal entity.
/// Used for CODO/DODO operating model sites where the site is operator-managed.
/// </summary>
public class Operator
{
    public Guid Id { get; set; }
    public Guid LegalEntityId { get; set; }
    public string OperatorCode { get; set; } = null!;
    public string OperatorName { get; set; } = null!;
    public string? TaxPayerId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset SyncedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation properties
    public LegalEntity LegalEntity { get; set; } = null!;
}

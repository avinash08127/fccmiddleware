namespace FccMiddleware.Domain.Entities;

/// <summary>
/// A fuel product (grade) available within a legal entity.
/// Product codes are unique per legal entity; synced from Databricks.
/// </summary>
public class Product
{
    public Guid Id { get; set; }
    public Guid LegalEntityId { get; set; }
    public string ProductCode { get; set; } = null!;
    public string ProductName { get; set; } = null!;
    public string UnitOfMeasure { get; set; } = "LITRE";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset SyncedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation properties
    public LegalEntity LegalEntity { get; set; } = null!;
    public ICollection<Nozzle> Nozzles { get; set; } = [];
}

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// A legal entity (tenant) representing a country-level operating company.
/// This is the root of the multi-tenancy hierarchy — every tenant-scoped table
/// has a foreign key back to this entity.
/// </summary>
public class LegalEntity
{
    public Guid Id { get; set; }
    public string CountryCode { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string CurrencyCode { get; set; } = null!;
    public string TaxAuthorityCode { get; set; } = null!;
    public bool FiscalizationRequired { get; set; }
    public string? FiscalizationProvider { get; set; }
    public string DefaultTimezone { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? DeactivatedAt { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<Site> Sites { get; set; } = [];
    public ICollection<Product> Products { get; set; } = [];
    public ICollection<Operator> Operators { get; set; } = [];
}

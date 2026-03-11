namespace FccMiddleware.Domain.Entities;

/// <summary>
/// A nozzle on a pump. Each nozzle dispenses one product (fuel grade).
/// Maintains Odoo↔FCC nozzle number mapping for pre-auth translation.
/// LegalEntityId and SiteId are denormalized for query filter performance.
/// </summary>
public class Nozzle
{
    public Guid Id { get; set; }
    public Guid PumpId { get; set; }
    public Guid SiteId { get; set; }
    public Guid LegalEntityId { get; set; }

    /// <summary>Nozzle number as known to Odoo POS (sent on pre-auth requests).</summary>
    public int OdooNozzleNumber { get; set; }

    /// <summary>Nozzle number forwarded to the FCC in pre-auth commands.</summary>
    public int FccNozzleNumber { get; set; }

    public Guid ProductId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset SyncedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation properties
    public Pump Pump { get; set; } = null!;
    public Site Site { get; set; } = null!;
    public LegalEntity LegalEntity { get; set; } = null!;
    public Product Product { get; set; } = null!;
}

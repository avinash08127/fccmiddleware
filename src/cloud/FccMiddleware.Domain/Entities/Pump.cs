namespace FccMiddleware.Domain.Entities;

/// <summary>
/// A fuel dispenser pump at a site.
/// Maintains two pump numbers: the Odoo POS number and the FCC vendor number,
/// which may differ after FCC replacement or re-numbering.
/// LegalEntityId is denormalized for global query filter performance.
/// </summary>
public class Pump
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid LegalEntityId { get; set; }

    /// <summary>Pump number as known to Odoo POS (source of truth from Databricks).</summary>
    public int PumpNumber { get; set; }

    /// <summary>Pump number sent to the Forecourt Controller.</summary>
    public int FccPumpNumber { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset SyncedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation properties
    public Site Site { get; set; } = null!;
    public LegalEntity LegalEntity { get; set; } = null!;
    public ICollection<Nozzle> Nozzles { get; set; } = [];
}

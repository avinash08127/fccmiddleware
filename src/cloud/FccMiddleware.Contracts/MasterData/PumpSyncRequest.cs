namespace FccMiddleware.Contracts.MasterData;

/// <summary>
/// Request body for PUT /api/v1/master-data/pumps.
/// </summary>
public sealed class PumpSyncRequest
{
    public List<PumpRecord> Pumps { get; init; } = [];
}

/// <summary>
/// A single pump record sent by Databricks, including its nozzles.
/// Upsert key: id.
/// </summary>
public sealed class PumpRecord
{
    public Guid Id { get; init; }

    /// <summary>Site code (globally unique). Used to resolve SiteId and LegalEntityId.</summary>
    public string SiteCode { get; init; } = null!;

    /// <summary>Odoo pump number (source of truth from Databricks).</summary>
    public int PumpNumber { get; init; }

    /// <summary>Nozzles on this pump.</summary>
    public List<NozzleRecord> Nozzles { get; init; } = [];

    public bool IsActive { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// A nozzle on a pump sent as part of the pump sync payload.
/// </summary>
public sealed class NozzleRecord
{
    /// <summary>Nozzle number (both Odoo and FCC use the same number unless remapped).</summary>
    public int NozzleNumber { get; init; }

    /// <summary>Canonical fuel product code (e.g., PMS, AGO, DPK).</summary>
    public string CanonicalProductCode { get; init; } = null!;

    /// <summary>Optional Odoo pump identifier (informational; not stored on nozzle).</summary>
    public string? OdooPumpId { get; init; }
}

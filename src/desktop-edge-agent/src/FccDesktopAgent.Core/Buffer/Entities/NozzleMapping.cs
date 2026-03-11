namespace FccDesktopAgent.Core.Buffer.Entities;

/// <summary>
/// Bidirectional mapping between Odoo pump/nozzle numbers and FCC pump/nozzle numbers.
/// Received from cloud configuration; used to translate pre-auth commands before sending to FCC.
/// </summary>
public sealed class NozzleMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string SiteCode { get; set; } = string.Empty;

    public int OdooPumpNumber { get; set; }
    public int FccPumpNumber { get; set; }

    public int OdooNozzleNumber { get; set; }
    public int FccNozzleNumber { get; set; }

    public string ProductCode { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset? SyncedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace FccDesktopAgent.Core.MasterData.Models;

/// <summary>
/// Locally cached pump record derived from nozzle mappings in the cloud config.
/// Stores the Odoo-to-FCC pump number mapping.
/// </summary>
public sealed class LocalPump
{
    public int OdooPumpNumber { get; set; }
    public int FccPumpNumber { get; set; }
}

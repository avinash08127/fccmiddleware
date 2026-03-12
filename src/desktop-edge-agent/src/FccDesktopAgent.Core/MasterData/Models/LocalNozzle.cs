namespace FccDesktopAgent.Core.MasterData.Models;

/// <summary>
/// Locally cached nozzle record from the cloud config.
/// Maps Odoo nozzle/pump numbers to FCC-native numbers plus the product code.
/// </summary>
public sealed class LocalNozzle
{
    public int OdooNozzleNumber { get; set; }
    public int OdooPumpNumber { get; set; }
    public int FccNozzleNumber { get; set; }
    public int FccPumpNumber { get; set; }
    public string ProductCode { get; set; } = string.Empty;
}

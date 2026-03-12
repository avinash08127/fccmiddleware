namespace FccDesktopAgent.Core.MasterData.Models;

/// <summary>
/// Root model wrapping all site equipment data plus the last sync timestamp.
/// Serialized to <c>site-data.json</c> in the agent data directory.
/// </summary>
public sealed class SiteDataSnapshot
{
    public SiteInfo Site { get; set; } = new();
    public List<LocalProduct> Products { get; set; } = [];
    public List<LocalPump> Pumps { get; set; } = [];
    public List<LocalNozzle> Nozzles { get; set; } = [];
    public DateTimeOffset LastSyncedUtc { get; set; }
}

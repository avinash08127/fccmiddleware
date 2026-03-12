namespace FccDesktopAgent.Core.MasterData.Models;

/// <summary>
/// Site identity and FCC configuration metadata.
/// Populated from cloud config after registration.
/// </summary>
public sealed class SiteInfo
{
    public string SiteCode { get; set; } = string.Empty;
    public string LegalEntityCode { get; set; } = string.Empty;
    public string Timezone { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = string.Empty;
    public string OperatingModel { get; set; } = string.Empty;
    public string? FccVendor { get; set; }
    public string? IngestionMode { get; set; }
}

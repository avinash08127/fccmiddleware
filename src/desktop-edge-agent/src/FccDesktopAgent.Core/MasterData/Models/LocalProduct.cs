namespace FccDesktopAgent.Core.MasterData.Models;

/// <summary>
/// Locally cached product mapping from the cloud config.
/// Maps FCC-native product codes to canonical (Odoo) product codes.
/// </summary>
public sealed class LocalProduct
{
    public string FccProductCode { get; set; } = string.Empty;
    public string CanonicalProductCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
}

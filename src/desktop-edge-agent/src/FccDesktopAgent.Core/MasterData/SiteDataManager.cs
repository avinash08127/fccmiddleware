using System.Text.Json;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.MasterData.Models;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.MasterData;

/// <summary>
/// Extracts site equipment data from <see cref="SiteConfig"/> and persists it
/// to <c>site-data.json</c> in the agent data directory.
///
/// Called after registration (first config received) and after each
/// successful config pull that yields a new config version.
/// </summary>
public sealed class SiteDataManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private const string FileName = "site-data.json";
    private readonly ILogger<SiteDataManager> _logger;
    private readonly object _lock = new();
    private SiteDataSnapshot? _cached;

    public SiteDataManager(ILogger<SiteDataManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extracts products, pumps, nozzles, and site info from <paramref name="config"/>
    /// and persists to <c>site-data.json</c> in the agent data directory.
    /// </summary>
    public void SyncFromConfig(SiteConfig config)
    {
        var snapshot = new SiteDataSnapshot
        {
            LastSyncedUtc = DateTimeOffset.UtcNow,
            Site = new SiteInfo
            {
                SiteCode = config.Identity?.SiteCode ?? string.Empty,
                LegalEntityCode = config.Identity?.LegalEntityId ?? string.Empty,
                Timezone = config.Site?.Timezone ?? string.Empty,
                CurrencyCode = config.Site?.Currency ?? string.Empty,
                OperatingModel = config.Site?.OperatingModel ?? string.Empty,
                FccVendor = config.Fcc?.Vendor,
                IngestionMode = config.Fcc?.IngestionMode,
            },
        };

        // Products from mappings
        if (config.Mappings?.Products is { Count: > 0 } products)
        {
            snapshot.Products = products.Select(p => new LocalProduct
            {
                FccProductCode = p.FccProductCode,
                CanonicalProductCode = p.CanonicalProductCode,
                DisplayName = p.DisplayName,
                Active = p.Active,
            }).ToList();
        }

        // Derive unique pumps from nozzle mappings
        if (config.Mappings?.Nozzles is { Count: > 0 } nozzles)
        {
            snapshot.Pumps = nozzles
                .Select(n => new { n.OdooPumpNumber, n.FccPumpNumber })
                .DistinctBy(p => p.OdooPumpNumber)
                .Select(p => new LocalPump
                {
                    OdooPumpNumber = p.OdooPumpNumber,
                    FccPumpNumber = p.FccPumpNumber,
                })
                .ToList();

            snapshot.Nozzles = nozzles.Select(n => new LocalNozzle
            {
                OdooNozzleNumber = n.OdooNozzleNumber,
                OdooPumpNumber = n.OdooPumpNumber,
                FccNozzleNumber = n.FccNozzleNumber,
                FccPumpNumber = n.FccPumpNumber,
                ProductCode = n.ProductCode,
            }).ToList();
        }

        var path = GetFilePath();
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(path, json);

        lock (_lock) _cached = snapshot;

        _logger.LogInformation(
            "Site data saved: {ProductCount} products, {PumpCount} pumps, {NozzleCount} nozzles",
            snapshot.Products.Count, snapshot.Pumps.Count, snapshot.Nozzles.Count);
    }

    /// <summary>
    /// Loads the site data snapshot from <c>site-data.json</c>.
    /// Returns <c>null</c> if the file does not exist (first boot before registration).
    /// </summary>
    public SiteDataSnapshot? LoadSiteData()
    {
        lock (_lock)
        {
            if (_cached is not null)
                return _cached;
        }

        var path = GetFilePath();
        if (!File.Exists(path))
        {
            _logger.LogDebug("No site-data.json found — site data not yet available");
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var snapshot = JsonSerializer.Deserialize<SiteDataSnapshot>(json, JsonOptions);
            lock (_lock) _cached = snapshot;

            if (snapshot is not null)
            {
                _logger.LogInformation(
                    "Site data loaded: {ProductCount} products, {PumpCount} pumps, {NozzleCount} nozzles",
                    snapshot.Products.Count, snapshot.Pumps.Count, snapshot.Nozzles.Count);
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Failed to read site-data.json");
            return null;
        }
    }

    private static string GetFilePath() =>
        Path.Combine(AgentDataDirectory.Resolve(), FileName);
}

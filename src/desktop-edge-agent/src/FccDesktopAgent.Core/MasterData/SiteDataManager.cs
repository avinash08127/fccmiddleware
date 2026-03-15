using System.Text.Json;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.MasterData.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SiteDataManager> _logger;
    private readonly object _lock = new();
    private SiteDataSnapshot? _cached;

    public SiteDataManager(IServiceScopeFactory scopeFactory, ILogger<SiteDataManager> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Extracts products, pumps, nozzles, and site info from <paramref name="config"/>
    /// and persists to <c>site-data.json</c> in the agent data directory.
    /// </summary>
    public async Task SyncFromConfigAsync(SiteConfig config)
    {
        var snapshot = new SiteDataSnapshot
        {
            LastSyncedUtc = DateTimeOffset.UtcNow,
            Site = new SiteInfo
            {
                SiteCode = config.Identity?.SiteCode ?? string.Empty,
                LegalEntityCode = config.Identity?.LegalEntityCode ?? string.Empty,
                Timezone = config.Identity?.Timezone ?? string.Empty,
                CurrencyCode = config.Identity?.CurrencyCode ?? string.Empty,
                OperatingModel = config.Site?.OperatingModel ?? string.Empty,
                FccVendor = config.Fcc?.Vendor,
                IngestionMode = config.Fcc?.IngestionMode,
            },
        };

        // Products from mappings
        if (config.Mappings?.Products is { Length: > 0 } products)
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
        if (config.Mappings?.Nozzles is { Length: > 0 } nozzles)
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
        var dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        // P-DSK-017: Use async file I/O to avoid blocking the calling thread
        await File.WriteAllTextAsync(path, json);

        lock (_lock) _cached = snapshot;

        _logger.LogInformation(
            "Site data saved: {ProductCount} products, {PumpCount} pumps, {NozzleCount} nozzles",
            snapshot.Products.Count, snapshot.Pumps.Count, snapshot.Nozzles.Count);
    }

    /// <summary>
    /// Loads the site data snapshot from <c>site-data.json</c>.
    /// Returns <c>null</c> if the file does not exist (first boot before registration).
    /// </summary>
    public async Task<SiteDataSnapshot?> LoadSiteDataAsync()
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
            // P-DSK-017: Use async file I/O to avoid blocking the calling thread
            var json = await File.ReadAllTextAsync(path);
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

    /// <summary>
    /// F-DSK-029: Upserts nozzle mappings from <paramref name="config"/> into
    /// the <c>nozzles</c> DB table so that <see cref="PreAuth.PreAuthHandler"/>
    /// can resolve Odoo→FCC pump/nozzle translations.
    /// </summary>
    public async Task SyncNozzleMappingsToDbAsync(SiteConfig config, CancellationToken ct)
    {
        var nozzles = config.Mappings?.Nozzles;
        if (nozzles is not { Length: > 0 })
            return;

        var siteCode = config.Identity?.SiteCode ?? string.Empty;
        var now = DateTimeOffset.UtcNow;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        foreach (var n in nozzles)
        {
            var existing = await db.NozzleMappings.FirstOrDefaultAsync(
                e => e.SiteCode == siteCode
                     && e.OdooPumpNumber == n.OdooPumpNumber
                     && e.OdooNozzleNumber == n.OdooNozzleNumber,
                ct);

            if (existing is null)
            {
                db.NozzleMappings.Add(new NozzleMapping
                {
                    SiteCode = siteCode,
                    OdooPumpNumber = n.OdooPumpNumber,
                    FccPumpNumber = n.FccPumpNumber,
                    OdooNozzleNumber = n.OdooNozzleNumber,
                    FccNozzleNumber = n.FccNozzleNumber,
                    ProductCode = n.ProductCode,
                    IsActive = true,
                    SyncedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                existing.FccPumpNumber = n.FccPumpNumber;
                existing.FccNozzleNumber = n.FccNozzleNumber;
                existing.ProductCode = n.ProductCode;
                existing.IsActive = true;
                existing.SyncedAt = now;
                existing.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Synced {Count} nozzle mapping(s) to DB for site {SiteCode}",
            nozzles.Length, siteCode);
    }

    public void Clear()
    {
        lock (_lock) _cached = null;

        var path = GetFilePath();
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to delete site-data.json during local reset");
        }
    }

    private static string GetFilePath() =>
        Path.Combine(AgentDataDirectory.Resolve(), FileName);
}

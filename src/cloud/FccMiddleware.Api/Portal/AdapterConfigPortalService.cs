using System.Text.Json;
using System.Text.Json.Nodes;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FccMiddleware.Api.Portal;

public sealed class AdapterConfigPortalService
{
    private readonly FccMiddlewareDbContext _db;
    private readonly AdapterCatalogService _catalog;

    public AdapterConfigPortalService(
        FccMiddlewareDbContext db,
        AdapterCatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<AdapterSummaryDto>> GetAdapterSummariesAsync(
        Guid legalEntityId,
        CancellationToken ct)
    {
        var defaultRows = (await _db.AdapterDefaultConfigs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.LegalEntityId == legalEntityId)
            .ToListAsync(ct))
            .ToDictionary(item => item.AdapterKey, item => item, StringComparer.OrdinalIgnoreCase);

        var activeSiteCounts = await _db.FccConfigs
            .IgnoreQueryFilters()
            .Where(item => item.LegalEntityId == legalEntityId && item.IsActive)
            .GroupBy(item => item.FccVendor)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);

        return _catalog.GetAll()
            .Select(entry =>
            {
                defaultRows.TryGetValue(entry.AdapterKey, out var row);
                activeSiteCounts.TryGetValue(entry.Vendor, out var siteCount);

                return new AdapterSummaryDto
                {
                    AdapterKey = entry.AdapterKey,
                    DisplayName = entry.DisplayName,
                    Vendor = entry.Vendor.ToString(),
                    AdapterVersion = entry.AdapterVersion,
                    SupportedProtocols = entry.SupportedProtocols,
                    SupportedIngestionMethods = entry.SupportedIngestionMethods.Select(item => item.ToString()).ToList(),
                    SupportsPreAuth = entry.SupportsPreAuth,
                    SupportsPumpStatus = entry.SupportsPumpStatus,
                    ActiveSiteCount = siteCount,
                    DefaultConfigVersion = row?.ConfigVersion ?? 0,
                    DefaultUpdatedAt = row?.UpdatedAt,
                    DefaultUpdatedBy = row?.UpdatedBy
                };
            })
            .ToList();
    }

    public async Task<AdapterDetailDto> GetAdapterDetailAsync(
        Guid legalEntityId,
        AdapterCatalogEntry entry,
        CancellationToken ct)
    {
        var defaultDoc = await GetDefaultConfigAsync(legalEntityId, entry, ct);

        var sites = await _db.Sites
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(site => site.LegalEntityId == legalEntityId
                && site.IsActive
                && site.FccConfigs.Any(config => config.IsActive && config.FccVendor == entry.Vendor))
            .Select(site => new
            {
                site.Id,
                site.SiteCode,
                site.SiteName
            })
            .ToListAsync(ct);

        var overrides = await _db.SiteAdapterOverrides
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.LegalEntityId == legalEntityId && item.AdapterKey == entry.AdapterKey)
            .ToDictionaryAsync(item => item.SiteId, ct);

        return new AdapterDetailDto
        {
            Schema = _catalog.ToSchemaDto(entry),
            DefaultConfig = defaultDoc,
            Sites = sites
                .OrderBy(item => item.SiteCode, StringComparer.OrdinalIgnoreCase)
                .Select(site =>
                {
                    overrides.TryGetValue(site.Id, out var row);
                    return new AdapterSiteUsageDto
                    {
                        SiteId = site.Id,
                        SiteCode = site.SiteCode,
                        SiteName = site.SiteName,
                        HasOverride = row is not null,
                        OverrideVersion = row?.ConfigVersion,
                        OverrideUpdatedAt = row?.UpdatedAt,
                        OverrideUpdatedBy = row?.UpdatedBy
                    };
                })
                .ToList()
        };
    }

    public async Task<AdapterConfigDocumentDto> GetDefaultConfigAsync(
        Guid legalEntityId,
        AdapterCatalogEntry entry,
        CancellationToken ct)
    {
        var row = await _db.AdapterDefaultConfigs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.LegalEntityId == legalEntityId && item.AdapterKey == entry.AdapterKey, ct);

        var current = BuildDefaultValues(row, entry);
        return new AdapterConfigDocumentDto
        {
            AdapterKey = entry.AdapterKey,
            LegalEntityId = legalEntityId,
            ConfigVersion = row?.ConfigVersion ?? 0,
            UpdatedAt = row?.UpdatedAt,
            UpdatedBy = row?.UpdatedBy,
            Values = AdapterConfigJson.ToElement(AdapterConfigJson.RedactSecrets(current, entry.Fields)),
            SecretState = AdapterConfigJson.ToElement(AdapterConfigJson.BuildSecretState(current, entry.Fields))
        };
    }

    public async Task<AdapterConfigDocumentDto> UpsertDefaultConfigAsync(
        Guid legalEntityId,
        AdapterCatalogEntry entry,
        JsonElement requestedValues,
        string updatedBy,
        string reason,
        CancellationToken ct)
    {
        var row = await _db.AdapterDefaultConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.LegalEntityId == legalEntityId && item.AdapterKey == entry.AdapterKey, ct);

        var before = BuildDefaultValues(row, entry);
        var normalized = AdapterConfigJson.Normalize(requestedValues, entry, defaultsOnly: true);
        var after = AdapterConfigJson.Merge(before, normalized);

        if (AdapterConfigJson.Serialize(before) == AdapterConfigJson.Serialize(after))
        {
            return await GetDefaultConfigAsync(legalEntityId, entry, ct);
        }

        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new AdapterDefaultConfig
            {
                Id = Guid.NewGuid(),
                LegalEntityId = legalEntityId,
                AdapterKey = entry.AdapterKey,
                FccVendor = entry.Vendor,
                ConfigJson = AdapterConfigJson.Serialize(after),
                ConfigVersion = 1,
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedBy = updatedBy
            };
            _db.AdapterDefaultConfigs.Add(row);
        }
        else
        {
            row.ConfigJson = AdapterConfigJson.Serialize(after);
            row.ConfigVersion += 1;
            row.UpdatedAt = now;
            row.UpdatedBy = updatedBy;
        }

        var matchingConfigs = await _db.FccConfigs
            .IgnoreQueryFilters()
            .Where(item => item.LegalEntityId == legalEntityId && item.IsActive && item.FccVendor == entry.Vendor)
            .ToListAsync(ct);

        var overrideRows = await _db.SiteAdapterOverrides
            .IgnoreQueryFilters()
            .Where(item => item.LegalEntityId == legalEntityId && item.AdapterKey == entry.AdapterKey)
            .ToDictionaryAsync(item => item.SiteId, ct);

        foreach (var config in matchingConfigs)
        {
            var siteValues = AdapterConfigJson.ReadSiteValues(config, entry);
            var siteOnly = AdapterConfigJson.Pick(siteValues, entry.Fields.Where(field => field.SiteConfigurable && !field.Defaultable));

            overrideRows.TryGetValue(config.SiteId, out var overrideRow);
            var overrideValues = overrideRow is null
                ? new JsonObject()
                : AdapterConfigJson.ParseObject(overrideRow.OverrideJson);

            var effective = AdapterConfigJson.Merge(AdapterConfigJson.Merge(after, siteOnly), overrideValues);
            AdapterConfigJson.ApplyToFccConfig(config, effective, entry);
        }

        _db.AuditEvents.Add(BuildAuditEvent(
            legalEntityId,
            siteCode: null,
            entityId: row.Id,
            eventType: "AdapterDefaultConfigUpdated",
            updatedBy: updatedBy,
            payload: new JsonObject
            {
                ["adapterKey"] = entry.AdapterKey,
                ["vendor"] = entry.Vendor.ToString(),
                ["reason"] = reason,
                ["beforeVersion"] = JsonValue.Create(Math.Max(0, row.ConfigVersion - 1)),
                ["afterVersion"] = JsonValue.Create(row.ConfigVersion),
                ["changes"] = AdapterConfigJson.BuildAuditDiff(before, after, entry.Fields)
            }));

        await _db.SaveChangesAsync(ct);
        return await GetDefaultConfigAsync(legalEntityId, entry, ct);
    }

    public async Task<SiteAdapterConfigDto?> GetSiteAdapterConfigAsync(Guid siteId, CancellationToken ct)
    {
        var site = await _db.Sites
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(item => item.FccConfigs.Where(config => config.IsActive))
            .FirstOrDefaultAsync(item => item.Id == siteId, ct);

        if (site is null)
        {
            return null;
        }

        var config = site.FccConfigs.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        if (config is null)
        {
            return null;
        }

        var entry = _catalog.Find(config.FccVendor);
        if (entry is null)
        {
            return null;
        }

        var defaultRow = await _db.AdapterDefaultConfigs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.LegalEntityId == site.LegalEntityId && item.AdapterKey == entry.AdapterKey, ct);

        var overrideRow = await _db.SiteAdapterOverrides
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.SiteId == site.Id && item.AdapterKey == entry.AdapterKey, ct);

        return BuildSiteConfigDto(site, config, entry, defaultRow, overrideRow);
    }

    public async Task<SiteAdapterConfigDto?> UpsertSiteAdapterConfigAsync(
        Guid siteId,
        JsonElement requestedValues,
        string updatedBy,
        string reason,
        CancellationToken ct)
    {
        var site = await _db.Sites
            .IgnoreQueryFilters()
            .Include(item => item.FccConfigs.Where(config => config.IsActive))
            .FirstOrDefaultAsync(item => item.Id == siteId, ct);

        if (site is null)
        {
            return null;
        }

        var config = site.FccConfigs.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        if (config is null)
        {
            return null;
        }

        var entry = _catalog.Find(config.FccVendor);
        if (entry is null)
        {
            return null;
        }

        var defaultRow = await _db.AdapterDefaultConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.LegalEntityId == site.LegalEntityId && item.AdapterKey == entry.AdapterKey, ct);

        var overrideRow = await _db.SiteAdapterOverrides
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.SiteId == site.Id && item.AdapterKey == entry.AdapterKey, ct);

        var defaultValues = BuildDefaultValues(defaultRow, entry);
        var currentSiteValues = AdapterConfigJson.ReadSiteValues(config, entry);
        var siteOnlyCurrent = AdapterConfigJson.Pick(currentSiteValues, entry.Fields.Where(field => field.SiteConfigurable && !field.Defaultable));
        var currentOverride = overrideRow is null ? new JsonObject() : AdapterConfigJson.ParseObject(overrideRow.OverrideJson);
        var beforeEffective = AdapterConfigJson.Merge(AdapterConfigJson.Merge(defaultValues, siteOnlyCurrent), currentOverride);

        var normalized = AdapterConfigJson.Normalize(requestedValues, entry, defaultsOnly: false);
        var afterEffective = AdapterConfigJson.Merge(beforeEffective, normalized);
        var afterSiteOnly = AdapterConfigJson.Pick(afterEffective, entry.Fields.Where(field => field.SiteConfigurable && !field.Defaultable));
        var afterDefaultable = AdapterConfigJson.Pick(afterEffective, entry.Fields.Where(field => field.Defaultable));
        var afterOverride = AdapterConfigJson.Diff(afterDefaultable, defaultValues, entry.Fields.Where(field => field.Defaultable));
        var finalEffective = AdapterConfigJson.Merge(AdapterConfigJson.Merge(defaultValues, afterSiteOnly), afterOverride);

        if (AdapterConfigJson.Serialize(beforeEffective) == AdapterConfigJson.Serialize(finalEffective))
        {
            return BuildSiteConfigDto(site, config, entry, defaultRow, overrideRow);
        }

        if (afterOverride.Count == 0)
        {
            if (overrideRow is not null)
            {
                _db.SiteAdapterOverrides.Remove(overrideRow);
                overrideRow = null;
            }
        }
        else if (overrideRow is null)
        {
            overrideRow = new SiteAdapterOverride
            {
                Id = Guid.NewGuid(),
                SiteId = site.Id,
                LegalEntityId = site.LegalEntityId,
                AdapterKey = entry.AdapterKey,
                FccVendor = entry.Vendor,
                OverrideJson = AdapterConfigJson.Serialize(afterOverride),
                ConfigVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = updatedBy
            };
            _db.SiteAdapterOverrides.Add(overrideRow);
        }
        else
        {
            overrideRow.OverrideJson = AdapterConfigJson.Serialize(afterOverride);
            overrideRow.ConfigVersion += 1;
            overrideRow.UpdatedAt = DateTimeOffset.UtcNow;
            overrideRow.UpdatedBy = updatedBy;
        }

        AdapterConfigJson.ApplyToFccConfig(config, finalEffective, entry);

        _db.AuditEvents.Add(BuildAuditEvent(
            site.LegalEntityId,
            site.SiteCode,
            site.Id,
            afterOverride.Count == 0 ? "SiteAdapterOverrideCleared" : "SiteAdapterOverrideSet",
            updatedBy,
            new JsonObject
            {
                ["adapterKey"] = entry.AdapterKey,
                ["vendor"] = entry.Vendor.ToString(),
                ["siteId"] = site.Id.ToString(),
                ["siteCode"] = site.SiteCode,
                ["reason"] = reason,
                ["changes"] = AdapterConfigJson.BuildAuditDiff(beforeEffective, finalEffective, entry.Fields)
            }));

        await _db.SaveChangesAsync(ct);
        return BuildSiteConfigDto(site, config, entry, defaultRow, overrideRow);
    }

    public async Task<SiteAdapterConfigDto?> ResetSiteAdapterConfigAsync(
        Guid siteId,
        string updatedBy,
        string reason,
        CancellationToken ct)
    {
        var site = await _db.Sites
            .IgnoreQueryFilters()
            .Include(item => item.FccConfigs.Where(config => config.IsActive))
            .FirstOrDefaultAsync(item => item.Id == siteId, ct);

        if (site is null)
        {
            return null;
        }

        var config = site.FccConfigs.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        if (config is null)
        {
            return null;
        }

        var entry = _catalog.Find(config.FccVendor);
        if (entry is null)
        {
            return null;
        }

        var defaultRow = await _db.AdapterDefaultConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.LegalEntityId == site.LegalEntityId && item.AdapterKey == entry.AdapterKey, ct);

        var overrideRow = await _db.SiteAdapterOverrides
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.SiteId == site.Id && item.AdapterKey == entry.AdapterKey, ct);

        if (overrideRow is null)
        {
            return BuildSiteConfigDto(site, config, entry, defaultRow, null);
        }

        var defaultValues = BuildDefaultValues(defaultRow, entry);
        var siteValues = AdapterConfigJson.ReadSiteValues(config, entry);
        var siteOnly = AdapterConfigJson.Pick(siteValues, entry.Fields.Where(field => field.SiteConfigurable && !field.Defaultable));
        var beforeEffective = AdapterConfigJson.Merge(AdapterConfigJson.Merge(defaultValues, siteOnly), AdapterConfigJson.ParseObject(overrideRow.OverrideJson));
        var afterEffective = AdapterConfigJson.Merge(defaultValues, siteOnly);

        _db.SiteAdapterOverrides.Remove(overrideRow);
        AdapterConfigJson.ApplyToFccConfig(config, afterEffective, entry);

        _db.AuditEvents.Add(BuildAuditEvent(
            site.LegalEntityId,
            site.SiteCode,
            site.Id,
            "SiteAdapterOverrideResetToDefault",
            updatedBy,
            new JsonObject
            {
                ["adapterKey"] = entry.AdapterKey,
                ["vendor"] = entry.Vendor.ToString(),
                ["siteId"] = site.Id.ToString(),
                ["siteCode"] = site.SiteCode,
                ["reason"] = reason,
                ["changes"] = AdapterConfigJson.BuildAuditDiff(beforeEffective, afterEffective, entry.Fields)
            }));

        await _db.SaveChangesAsync(ct);
        return BuildSiteConfigDto(site, config, entry, defaultRow, null);
    }

    private SiteAdapterConfigDto BuildSiteConfigDto(
        Site site,
        FccConfig config,
        AdapterCatalogEntry entry,
        AdapterDefaultConfig? defaultRow,
        SiteAdapterOverride? overrideRow)
    {
        var defaultValues = BuildDefaultValues(defaultRow, entry);
        var siteValues = AdapterConfigJson.ReadSiteValues(config, entry);
        var siteOnly = AdapterConfigJson.Pick(siteValues, entry.Fields.Where(field => field.SiteConfigurable && !field.Defaultable));
        var overrideValues = overrideRow is null ? new JsonObject() : AdapterConfigJson.ParseObject(overrideRow.OverrideJson);
        var effectiveValues = AdapterConfigJson.Merge(AdapterConfigJson.Merge(defaultValues, siteOnly), overrideValues);

        return new SiteAdapterConfigDto
        {
            SiteId = site.Id,
            LegalEntityId = site.LegalEntityId,
            SiteCode = site.SiteCode,
            SiteName = site.SiteName,
            AdapterKey = entry.AdapterKey,
            Vendor = entry.Vendor.ToString(),
            DefaultConfigVersion = defaultRow?.ConfigVersion ?? 0,
            OverrideVersion = overrideRow?.ConfigVersion,
            OverrideUpdatedAt = overrideRow?.UpdatedAt,
            OverrideUpdatedBy = overrideRow?.UpdatedBy,
            DefaultValues = AdapterConfigJson.ToElement(AdapterConfigJson.RedactSecrets(defaultValues, entry.Fields)),
            OverrideValues = AdapterConfigJson.ToElement(AdapterConfigJson.RedactSecrets(overrideValues, entry.Fields)),
            EffectiveValues = AdapterConfigJson.ToElement(AdapterConfigJson.RedactSecrets(effectiveValues, entry.Fields)),
            SecretState = AdapterConfigJson.ToElement(AdapterConfigJson.BuildSecretState(effectiveValues, entry.Fields)),
            FieldSources = AdapterConfigJson.ToElement(AdapterConfigJson.BuildFieldSources(overrideValues, entry.Fields)),
            Schema = _catalog.ToSchemaDto(entry)
        };
    }

    private JsonObject BuildDefaultValues(AdapterDefaultConfig? row, AdapterCatalogEntry entry)
    {
        var catalogDefaults = _catalog.BuildDefaultValues(entry);
        if (row is null)
        {
            return catalogDefaults;
        }

        return AdapterConfigJson.Merge(catalogDefaults, AdapterConfigJson.ParseObject(row.ConfigJson));
    }

    private static AuditEvent BuildAuditEvent(
        Guid legalEntityId,
        string? siteCode,
        Guid entityId,
        string eventType,
        string updatedBy,
        JsonObject payload)
    {
        payload["updatedBy"] = JsonValue.Create(updatedBy);
        payload["timestamp"] = JsonValue.Create(DateTimeOffset.UtcNow);

        return new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            LegalEntityId = legalEntityId,
            SiteCode = siteCode,
            EntityId = entityId,
            EventType = eventType,
            CorrelationId = Guid.NewGuid(),
            Source = "AdapterConfigPortalService",
            Payload = payload.ToJsonString(new JsonSerializerOptions(PortalJson.SerializerOptions)
            {
                WriteIndented = false
            })
        };
    }
}

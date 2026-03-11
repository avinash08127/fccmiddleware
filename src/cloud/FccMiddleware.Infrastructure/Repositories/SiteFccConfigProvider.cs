using FccMiddleware.Application.Ingestion;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Models.Adapter;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Infrastructure.Repositories;

/// <summary>
/// Resolves SiteFccConfig by joining FccConfig + Site + LegalEntity.
/// Currency code and timezone come from the LegalEntity (tenant-level defaults).
/// ApiKey is intentionally left empty — callers must resolve from Secrets Manager
/// before making outbound FCC calls.
/// </summary>
public sealed class SiteFccConfigProvider : ISiteFccConfigProvider
{
    private readonly FccMiddlewareDbContext _dbContext;
    private readonly ILogger<SiteFccConfigProvider> _logger;

    public SiteFccConfigProvider(
        FccMiddlewareDbContext dbContext,
        ILogger<SiteFccConfigProvider> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(SiteFccConfig Config, Guid LegalEntityId)?> GetBySiteCodeAsync(
        string siteCode,
        CancellationToken ct = default)
    {
        var row = await _dbContext.FccConfigs
            .IgnoreQueryFilters()
            .Where(cfg => cfg.Site.SiteCode == siteCode && cfg.IsActive)
            .Select(cfg => new
            {
                cfg.FccVendor,
                cfg.ConnectionProtocol,
                cfg.HostAddress,
                cfg.Port,
                cfg.IngestionMethod,
                cfg.PullIntervalSeconds,
                cfg.Site.LegalEntityId,
                cfg.LegalEntity.CurrencyCode,
                cfg.LegalEntity.DefaultTimezone
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            _logger.LogWarning("No active FccConfig found for site {SiteCode}", siteCode);
            return null;
        }

        var config = new SiteFccConfig
        {
            SiteCode = siteCode,
            FccVendor = row.FccVendor,
            ConnectionProtocol = row.ConnectionProtocol,
            HostAddress = row.HostAddress,
            Port = row.Port,
            // ApiKey intentionally empty — resolve from Secrets Manager before outbound calls
            ApiKey = string.Empty,
            IngestionMethod = row.IngestionMethod,
            PullIntervalSeconds = row.PullIntervalSeconds,
            CurrencyCode = row.CurrencyCode,
            Timezone = row.DefaultTimezone,
            PumpNumberOffset = 0,
            ProductCodeMapping = new Dictionary<string, string>()
        };

        return (config, row.LegalEntityId);
    }
}

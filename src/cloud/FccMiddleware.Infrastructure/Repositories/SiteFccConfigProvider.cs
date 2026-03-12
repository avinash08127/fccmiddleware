using System.Security.Cryptography;
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
        var row = await ProjectFccConfigRow(
            q => q.Where(cfg => cfg.Site.SiteCode == siteCode && cfg.IsActive),
            ct);

        if (row is null)
        {
            _logger.LogWarning("No active FccConfig found for site {SiteCode}", siteCode);
            return null;
        }

        return (BuildSiteFccConfig(row), row.LegalEntityId);
    }

    /// <inheritdoc />
    public async Task<(SiteFccConfig Config, Guid LegalEntityId)?> GetByUsnCodeAsync(
        int usnCode,
        CancellationToken ct = default)
    {
        var row = await ProjectFccConfigRow(
            q => q.Where(cfg => cfg.IsActive
                && cfg.FccVendor == FccVendor.RADIX
                && cfg.UsnCode == usnCode),
            ct);

        if (row is null)
        {
            _logger.LogWarning("No active Radix FccConfig found for USN code {UsnCode}", usnCode);
            return null;
        }

        return (BuildSiteFccConfig(row), row.LegalEntityId);
    }

    /// <inheritdoc />
    public async Task<(SiteFccConfig Config, Guid LegalEntityId)?> GetByWebhookSecretAsync(
        string webhookSecret,
        CancellationToken ct = default)
    {
        // Load all active Petronite configs that have a webhook secret set.
        // We use constant-time comparison in memory to avoid timing attacks.
        var candidates = await _dbContext.FccConfigs
            .IgnoreQueryFilters()
            .Where(cfg => cfg.IsActive
                && cfg.FccVendor == FccVendor.PETRONITE
                && cfg.WebhookSecret != null)
            .Select(cfg => new FccConfigProjection
            {
                SiteCode = cfg.Site.SiteCode,
                FccVendor = cfg.FccVendor,
                ConnectionProtocol = cfg.ConnectionProtocol,
                HostAddress = cfg.HostAddress,
                Port = cfg.Port,
                IngestionMethod = cfg.IngestionMethod,
                PullIntervalSeconds = cfg.PullIntervalSeconds,
                LegalEntityId = cfg.Site.LegalEntityId,
                CurrencyCode = cfg.LegalEntity.CurrencyCode,
                DefaultTimezone = cfg.LegalEntity.DefaultTimezone,
                SharedSecret = cfg.SharedSecret,
                UsnCode = cfg.UsnCode,
                AuthPort = cfg.AuthPort,
                ClientId = cfg.ClientId,
                ClientSecret = cfg.ClientSecret,
                WebhookSecret = cfg.WebhookSecret,
                OAuthTokenEndpoint = cfg.OAuthTokenEndpoint,
            })
            .ToListAsync(ct);

        var match = candidates.FirstOrDefault(c =>
            CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(c.WebhookSecret!),
                System.Text.Encoding.UTF8.GetBytes(webhookSecret)));

        if (match is null)
        {
            _logger.LogWarning("No active Petronite FccConfig matched the provided webhook secret");
            return null;
        }

        return (BuildSiteFccConfig(match), match.LegalEntityId);
    }

    // ── Shared projection + builder ───────────────────────────────────────────

    private async Task<FccConfigProjection?> ProjectFccConfigRow(
        Func<IQueryable<Domain.Entities.FccConfig>, IQueryable<Domain.Entities.FccConfig>> filter,
        CancellationToken ct)
    {
        return await filter(_dbContext.FccConfigs.IgnoreQueryFilters())
            .Select(cfg => new FccConfigProjection
            {
                SiteCode = cfg.Site.SiteCode,
                FccVendor = cfg.FccVendor,
                ConnectionProtocol = cfg.ConnectionProtocol,
                HostAddress = cfg.HostAddress,
                Port = cfg.Port,
                IngestionMethod = cfg.IngestionMethod,
                PullIntervalSeconds = cfg.PullIntervalSeconds,
                LegalEntityId = cfg.Site.LegalEntityId,
                CurrencyCode = cfg.LegalEntity.CurrencyCode,
                DefaultTimezone = cfg.LegalEntity.DefaultTimezone,
                SharedSecret = cfg.SharedSecret,
                UsnCode = cfg.UsnCode,
                AuthPort = cfg.AuthPort,
                ClientId = cfg.ClientId,
                ClientSecret = cfg.ClientSecret,
                WebhookSecret = cfg.WebhookSecret,
                OAuthTokenEndpoint = cfg.OAuthTokenEndpoint,
            })
            .FirstOrDefaultAsync(ct);
    }

    private static SiteFccConfig BuildSiteFccConfig(FccConfigProjection row) =>
        new()
        {
            SiteCode = row.SiteCode,
            FccVendor = row.FccVendor,
            ConnectionProtocol = row.ConnectionProtocol,
            HostAddress = row.HostAddress,
            Port = row.Port,
            ApiKey = string.Empty,
            IngestionMethod = row.IngestionMethod,
            PullIntervalSeconds = row.PullIntervalSeconds,
            CurrencyCode = row.CurrencyCode,
            Timezone = row.DefaultTimezone,
            PumpNumberOffset = 0,
            ProductCodeMapping = new Dictionary<string, string>(),
            SharedSecret = row.SharedSecret,
            UsnCode = row.UsnCode,
            AuthPort = row.AuthPort,
            ClientId = row.ClientId,
            ClientSecret = row.ClientSecret,
            WebhookSecret = row.WebhookSecret,
            OAuthTokenEndpoint = row.OAuthTokenEndpoint,
        };

    private sealed class FccConfigProjection
    {
        public required string SiteCode { get; init; }
        public required FccVendor FccVendor { get; init; }
        public required ConnectionProtocol ConnectionProtocol { get; init; }
        public required string HostAddress { get; init; }
        public required int Port { get; init; }
        public required IngestionMethod IngestionMethod { get; init; }
        public int? PullIntervalSeconds { get; init; }
        public required Guid LegalEntityId { get; init; }
        public required string CurrencyCode { get; init; }
        public required string DefaultTimezone { get; init; }
        public string? SharedSecret { get; init; }
        public int? UsnCode { get; init; }
        public int? AuthPort { get; init; }
        public string? ClientId { get; init; }
        public string? ClientSecret { get; init; }
        public string? WebhookSecret { get; init; }
        public string? OAuthTokenEndpoint { get; init; }
    }
}

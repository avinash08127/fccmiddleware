using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

        // M-11: Use HMAC-based comparison so the check is constant-time regardless
        // of input length. FixedTimeEquals short-circuits on different-length spans,
        // leaking the valid secret's length via timing analysis.
        var match = candidates.FirstOrDefault(c =>
            ConstantTimeSecretEquals(c.WebhookSecret!, webhookSecret));

        if (match is null)
        {
            _logger.LogWarning("No active Petronite FccConfig matched the provided webhook secret");
            return null;
        }

        return (BuildSiteFccConfig(match), match.LegalEntityId);
    }

    /// <inheritdoc />
    public async Task<(SiteFccConfig Config, Guid LegalEntityId)?> GetByAdvatecWebhookTokenAsync(
        string webhookToken,
        CancellationToken ct = default)
    {
        // Load all active Advatec configs that have a webhook token set.
        // We use constant-time comparison in memory to avoid timing attacks.
        var candidates = await _dbContext.FccConfigs
            .IgnoreQueryFilters()
            .Where(cfg => cfg.IsActive
                && cfg.FccVendor == FccVendor.ADVATEC
                && cfg.AdvatecWebhookToken != null)
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
                AdvatecWebhookToken = cfg.AdvatecWebhookToken,
            })
            .ToListAsync(ct);

        // M-11: Same HMAC-based constant-time comparison for Advatec tokens
        var match = candidates.FirstOrDefault(c =>
            ConstantTimeSecretEquals(c.AdvatecWebhookToken!, webhookToken));

        if (match is null)
        {
            _logger.LogWarning("No active Advatec FccConfig matched the provided webhook token");
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
                // H-14: Project Advatec fields (previously missing from shared projection)
                AdvatecWebhookToken = cfg.AdvatecWebhookToken,
                AdvatecDevicePort = cfg.AdvatecDevicePort,
                AdvatecEfdSerialNumber = cfg.AdvatecEfdSerialNumber,
                AdvatecCustIdType = cfg.AdvatecCustIdType,
                // M-14: Project DOMS/Radix vendor fields
                JplPort = cfg.JplPort,
                DppPorts = cfg.DppPorts,
                FcAccessCode = cfg.FcAccessCode,
                DomsCountryCode = cfg.DomsCountryCode,
                PosVersionId = cfg.PosVersionId,
                HeartbeatIntervalSeconds = cfg.HeartbeatIntervalSeconds,
                ReconnectBackoffMaxSeconds = cfg.ReconnectBackoffMaxSeconds,
                ConfiguredPumps = cfg.ConfiguredPumps,
                FccPumpAddressMapJson = cfg.FccPumpAddressMap,
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
            // H-14: Advatec vendor-specific fields
            AdvatecWebhookToken = row.AdvatecWebhookToken,
            AdvatecDevicePort = row.AdvatecDevicePort,
            AdvatecEfdSerialNumber = row.AdvatecEfdSerialNumber,
            AdvatecCustIdType = row.AdvatecCustIdType,
            // M-14: DOMS/Radix vendor-specific fields
            JplPort = row.JplPort,
            DppPorts = row.DppPorts,
            FcAccessCode = row.FcAccessCode,
            DomsCountryCode = row.DomsCountryCode,
            PosVersionId = row.PosVersionId,
            HeartbeatIntervalSeconds = row.HeartbeatIntervalSeconds,
            ReconnectBackoffMaxSeconds = row.ReconnectBackoffMaxSeconds,
            ConfiguredPumps = row.ConfiguredPumps,
            FccPumpAddressMap = ParsePumpAddressMap(row.FccPumpAddressMapJson),
        };

    /// <summary>
    /// M-14: Deserializes the FccPumpAddressMap JSON string into a typed dictionary.
    /// Returns null if the JSON is empty or malformed (fallback to offset-based pump resolution).
    /// </summary>
    private static IReadOnlyDictionary<int, RadixPumpAddress>? ParsePumpAddressMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, RadixPumpAddress>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// M-11: HMAC-based constant-time comparison. CryptographicOperations.FixedTimeEquals
    /// short-circuits on different-length spans, leaking the valid secret's length.
    /// By HMAC-hashing both inputs with the same key, we always compare fixed-length
    /// (32-byte) digests, eliminating the length side-channel.
    /// </summary>
    private static bool ConstantTimeSecretEquals(string stored, string provided)
    {
        var key = new byte[32]; // zeroed key is fine — we only need equal-length digests
        using var hmac1 = new HMACSHA256(key);
        using var hmac2 = new HMACSHA256(key);
        var hash1 = hmac1.ComputeHash(Encoding.UTF8.GetBytes(stored));
        var hash2 = hmac2.ComputeHash(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(hash1, hash2);
    }

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
        public string? AdvatecWebhookToken { get; init; }
        public int? AdvatecDevicePort { get; init; }
        public string? AdvatecEfdSerialNumber { get; init; }
        public int? AdvatecCustIdType { get; init; }
        // M-14: DOMS/Radix vendor fields
        public int? JplPort { get; init; }
        public string? DppPorts { get; init; }
        public string? FcAccessCode { get; init; }
        public string? DomsCountryCode { get; init; }
        public string? PosVersionId { get; init; }
        public int HeartbeatIntervalSeconds { get; init; }
        public int? ReconnectBackoffMaxSeconds { get; init; }
        public string? ConfiguredPumps { get; init; }
        public string? FccPumpAddressMapJson { get; init; }
    }
}

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
        // TX-P05: Query by SHA-256 hash of the webhook secret using indexed column,
        // instead of loading all Petronite configs into memory (O(N) scan).
        var secretHash = ComputeSha256Hex(webhookSecret);

        var row = await ProjectFccConfigRow(
            q => q.Where(cfg => cfg.IsActive
                && cfg.FccVendor == FccVendor.PETRONITE
                && cfg.WebhookSecretHash == secretHash),
            ct);

        if (row is null)
        {
            _logger.LogWarning("No active Petronite FccConfig matched the provided webhook secret");
            return null;
        }

        // Constant-time comparison against the actual stored secret to confirm match
        // (hash collision guard + M-11 timing-attack mitigation)
        if (row.WebhookSecret is null || !ConstantTimeSecretEquals(row.WebhookSecret, webhookSecret))
        {
            _logger.LogWarning("Petronite webhook secret hash matched but constant-time comparison failed");
            return null;
        }

        return (BuildSiteFccConfig(row), row.LegalEntityId);
    }

    /// <inheritdoc />
    public async Task<(SiteFccConfig Config, Guid LegalEntityId)?> GetByAdvatecWebhookTokenAsync(
        string webhookToken,
        CancellationToken ct = default)
    {
        // H-04: Query by SHA-256 hash of the webhook token using indexed column,
        // instead of loading all Advatec configs into memory (DOS vector).
        var tokenHash = ComputeSha256Hex(webhookToken);

        var row = await ProjectFccConfigRow(
            q => q.Where(cfg => cfg.IsActive
                && cfg.FccVendor == FccVendor.ADVATEC
                && cfg.AdvatecWebhookTokenHash == tokenHash),
            ct);

        if (row is null)
        {
            _logger.LogWarning("No active Advatec FccConfig matched the provided webhook token");
            return null;
        }

        // Constant-time comparison against the actual stored token to confirm match
        // (hash collision guard + timing-attack mitigation)
        if (row.AdvatecWebhookToken is null || !ConstantTimeSecretEquals(row.AdvatecWebhookToken, webhookToken))
        {
            _logger.LogWarning("Advatec webhook token hash matched but constant-time comparison failed");
            return null;
        }

        return (BuildSiteFccConfig(row), row.LegalEntityId);
    }

    /// <summary>
    /// Computes a lowercase hex SHA-256 hash for indexed token lookup (H-04).
    /// </summary>
    private static string ComputeSha256Hex(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
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
                AdvatecPumpMapJson = cfg.AdvatecPumpMap,
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

    private SiteFccConfig BuildSiteFccConfig(FccConfigProjection row)
    {
        ValidateVendorRequiredFields(row);

        return new SiteFccConfig
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
            AdvatecPumpMap = ParseAdvatecPumpMap(row.AdvatecPumpMapJson),
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
    }

    /// <summary>
    /// M-01: Validates that vendor-specific required fields are present at resolution time
    /// rather than failing silently at adapter runtime.
    /// </summary>
    private void ValidateVendorRequiredFields(FccConfigProjection row)
    {
        var errors = new List<string>();

        switch (row.FccVendor)
        {
            case FccVendor.RADIX:
                if (row.UsnCode is null)
                    errors.Add("UsnCode is required for Radix");
                if (row.AuthPort is null)
                    errors.Add("AuthPort is required for Radix");
                if (string.IsNullOrWhiteSpace(row.SharedSecret))
                    errors.Add("SharedSecret is required for Radix");
                break;

            case FccVendor.DOMS:
                if (row.JplPort is null)
                    errors.Add("JplPort is required for DOMS");
                break;

            case FccVendor.PETRONITE:
                if (string.IsNullOrWhiteSpace(row.ClientId))
                    errors.Add("ClientId is required for Petronite");
                if (string.IsNullOrWhiteSpace(row.ClientSecret))
                    errors.Add("ClientSecret is required for Petronite");
                break;

            case FccVendor.ADVATEC:
                // Advatec is push-only via webhook; no strictly required vendor fields
                break;
        }

        if (errors.Count > 0)
        {
            _logger.LogWarning(
                "FccConfig for site {SiteCode} (vendor {Vendor}) has missing required fields: {Errors}",
                row.SiteCode, row.FccVendor, string.Join("; ", errors));

            throw new InvalidOperationException(
                $"FccConfig for site {row.SiteCode} ({row.FccVendor}) is missing required fields: " +
                string.Join("; ", errors));
        }
    }

    /// <summary>
    /// Deserializes the FccPumpAddressMap JSON string into a typed dictionary.
    /// Returns null if the JSON is empty. Throws on malformed JSON to prevent
    /// silent fallback to offset-based pump resolution (M-03).
    /// </summary>
    private IReadOnlyDictionary<int, RadixPumpAddress>? ParsePumpAddressMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, RadixPumpAddress>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Failed to parse Radix FccPumpAddressMap JSON. Pump mapping will be incorrect. Raw: {Json}",
                json);
            throw new InvalidOperationException(
                $"Radix FccPumpAddressMap contains malformed JSON: {ex.Message}", ex);
        }
    }

    private IReadOnlyDictionary<string, int>? ParseAdvatecPumpMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Failed to parse Advatec PumpMap JSON. Pump mapping will be incorrect. Raw: {Json}",
                json);
            throw new InvalidOperationException(
                $"Advatec PumpMap contains malformed JSON: {ex.Message}", ex);
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
        public string? AdvatecPumpMapJson { get; init; }
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

using FccMiddleware.Application.Common;
using FccMiddleware.Contracts.Config;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace FccMiddleware.Application.AgentConfig;

/// <summary>
/// Builds a full SiteConfig snapshot from DB entities (fcc_configs, sites, legal_entities,
/// pumps, nozzles, products) and returns it to the Edge Agent.
/// Returns 304-equivalent when the client already has the current config version.
/// </summary>
public sealed class GetAgentConfigHandler
    : IRequestHandler<GetAgentConfigQuery, Result<GetAgentConfigResult>>
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly IAgentConfigDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;

    public GetAgentConfigHandler(IAgentConfigDbContext db, IConfiguration configuration, IMemoryCache cache)
    {
        _db = db;
        _configuration = configuration;
        _cache = cache;
    }

    public async Task<Result<GetAgentConfigResult>> Handle(
        GetAgentConfigQuery request,
        CancellationToken cancellationToken)
    {
        // Verify the device exists and is active
        var agent = await _db.FindAgentByDeviceIdAsync(request.DeviceId, cancellationToken);
        if (agent is null || !agent.IsActive)
        {
            return Result<GetAgentConfigResult>.Failure("DEVICE_NOT_FOUND",
                "No active agent registration found for this device.");
        }

        // Verify the device is registered at the claimed site
        if (agent.SiteCode != request.SiteCode)
        {
            return Result<GetAgentConfigResult>.Failure("SITE_MISMATCH",
                "Device is not registered at the claimed site.");
        }

        if (agent.LegalEntityId != request.LegalEntityId)
        {
            return Result<GetAgentConfigResult>.Failure("SITE_MISMATCH",
                "Device is not registered under the claimed legal entity.");
        }

        // ── OB-P01: Cache the site-level snapshot per (siteCode, legalEntityId).
        // Identity and SiteHa are stamped per-device/per-request because peer membership
        // changes independently of FCC/site configuration edits.
        var cacheKey = $"agent-config:{request.SiteCode}:{request.LegalEntityId}";

        if (!_cache.TryGetValue(cacheKey, out (int ConfigVersion, SiteConfigResponse Config) cached))
        {
            // Cache miss — load full entity graph from DB
            var fccConfig = await _db.GetFccConfigWithSiteDataAsync(
                request.SiteCode, request.LegalEntityId, cancellationToken);

            if (fccConfig is null)
            {
                return Result<GetAgentConfigResult>.Failure("CONFIG_NOT_FOUND",
                    "No active FCC configuration found for this site.");
            }

            var site = fccConfig.Site;
            var legalEntity = fccConfig.LegalEntity;
            var issuedAt = fccConfig.UpdatedAt;

            var config = new SiteConfigResponse
            {
                SchemaVersion = "1.0",
                ConfigVersion = fccConfig.ConfigVersion,
                ConfigId = fccConfig.Id,
                IssuedAtUtc = issuedAt,
                EffectiveAtUtc = issuedAt,
                SourceRevision = new SourceRevisionDto
                {
                    DatabricksSyncAtUtc = site.SyncedAt,
                    SiteMasterRevision = site.UpdatedAt.ToString("O"),
                    FccConfigRevision = fccConfig.UpdatedAt.ToString("O"),
                    PortalChangeId = null
                },
                Identity = new IdentityDto
                {
                    LegalEntityId = legalEntity.Id,
                    LegalEntityCode = legalEntity.CountryCode,
                    SiteId = site.Id,
                    SiteCode = site.SiteCode,
                    SiteName = site.SiteName,
                    Timezone = legalEntity.DefaultTimezone,
                    CurrencyCode = legalEntity.CurrencyCode,
                    DeviceId = string.Empty, // placeholder — overridden per-device below
                    DeviceClass = "ANDROID",
                    IsPrimaryAgent = true
                },
                Site = new SiteDto
                {
                    IsActive = site.IsActive,
                    OperatingModel = site.OperatingModel.ToString(),
                    SiteUsesPreAuth = site.SiteUsesPreAuth,
                    ConnectivityMode = site.ConnectivityMode,
                    OdooSiteId = site.OdooSiteId ?? string.Empty,
                    CompanyTaxPayerId = site.CompanyTaxPayerId ?? string.Empty,
                    OperatorName = site.OperatorName,
                    OperatorTaxPayerId = site.OperatorTaxPayerId
                },
                Fcc = BuildFccDto(fccConfig),
                Sync = BuildSyncDto(),
                Buffer = BuildBufferDto(),
                LocalApi = BuildLocalApiDto(),
                SiteHa = BuildPlaceholderSiteHa(),
                Telemetry = BuildTelemetryDto(),
                Fiscalization = BuildFiscalizationDto(site),
                Mappings = await BuildMappingsDtoAsync(fccConfig, cancellationToken),
                Rollout = BuildRolloutDto()
            };

            cached = (fccConfig.ConfigVersion, config);
            _cache.Set(cacheKey, cached, CacheDuration);
        }

        // ETag comparison: return 304 if client already has the current version
        if (request.ClientConfigVersion.HasValue
            && request.ClientConfigVersion.Value == cached.ConfigVersion)
        {
            return Result<GetAgentConfigResult>.Success(new GetAgentConfigResult
            {
                NotModified = true,
                ConfigVersion = cached.ConfigVersion
            });
        }

        var siteAgents = await _db.GetSiteAgentsAsync(cached.Config.Identity.SiteId, cancellationToken);
        var siteHa = BuildSiteHaDto(agent, siteAgents);

        // Stamp per-device Identity (records are immutable; `with` creates a shallow copy)
        var deviceConfig = cached.Config with
        {
            Identity = cached.Config.Identity with
            {
                DeviceId = request.DeviceId.ToString(),
                DeviceClass = agent.DeviceClass,
                IsPrimaryAgent = string.Equals(siteHa.CurrentRole, "PRIMARY", StringComparison.Ordinal)
            },
            SiteHa = siteHa
        };

        return Result<GetAgentConfigResult>.Success(new GetAgentConfigResult
        {
            NotModified = false,
            ConfigVersion = cached.ConfigVersion,
            Config = deviceConfig
        });
    }

    private static SiteHaDto BuildPlaceholderSiteHa() =>
        new()
        {
            Enabled = false,
            AutoFailoverEnabled = false,
            Priority = 100,
            RoleCapability = "PRIMARY_ELIGIBLE",
            CurrentRole = "STANDBY_HOT",
            HeartbeatIntervalSeconds = 5,
            FailoverTimeoutSeconds = 30,
            MaxReplicationLagSeconds = 15,
            PeerDiscoveryMode = "HYBRID",
            AllowFailback = false,
            LeaderEpoch = 0,
            PeerDirectory = []
        };

    private static FccDto BuildFccDto(Domain.Entities.FccConfig fccConfig)
    {
        var enabled = fccConfig.IsActive;
        var pullIntervalSeconds = enabled ? fccConfig.PullIntervalSeconds : null;
        int? catchUpPullIntervalSeconds = enabled && fccConfig.IngestionMode == Domain.Enums.IngestionMode.CLOUD_DIRECT
            ? fccConfig.PullIntervalSeconds ?? 30
            : null;
        int? hybridCatchUpIntervalSeconds = enabled && fccConfig.IngestionMethod == Domain.Enums.IngestionMethod.HYBRID
            ? Math.Max(fccConfig.PullIntervalSeconds ?? 30, 30)
            : null;

        return new FccDto
        {
            Enabled = enabled,
            FccId = enabled ? fccConfig.Id : null,
            Vendor = enabled ? fccConfig.FccVendor.ToString() : null,
            Model = fccConfig.FccModel,
            Version = null,
            ConnectionProtocol = enabled ? fccConfig.ConnectionProtocol.ToString() : null,
            HostAddress = enabled ? fccConfig.HostAddress : null,
            Port = enabled ? fccConfig.Port : null,
            CredentialRef = enabled ? fccConfig.CredentialRef : null,
            CredentialRevision = null,
            SecretEnvelope = new SecretEnvelopeDto { Format = "NONE", Payload = null },
            TransactionMode = enabled ? fccConfig.IngestionMethod.ToString() : null,
            IngestionMode = enabled ? fccConfig.IngestionMode.ToString() : null,
            PullIntervalSeconds = pullIntervalSeconds,
            CatchUpPullIntervalSeconds = catchUpPullIntervalSeconds,
            HybridCatchUpIntervalSeconds = hybridCatchUpIntervalSeconds,
            HeartbeatIntervalSeconds = fccConfig.HeartbeatIntervalSeconds,
            HeartbeatTimeoutSeconds = fccConfig.HeartbeatIntervalSeconds * 3,
            PushSourceIpAllowList = [],
            JplPort = fccConfig.JplPort,
            FcAccessCode = fccConfig.FcAccessCode,
            DomsCountryCode = fccConfig.DomsCountryCode,
            PosVersionId = fccConfig.PosVersionId,
            ConfiguredPumps = fccConfig.ConfiguredPumps,
            DppPorts = fccConfig.DppPorts,
            ReconnectBackoffMaxSeconds = fccConfig.ReconnectBackoffMaxSeconds,
            SharedSecret = fccConfig.SharedSecret,
            UsnCode = fccConfig.UsnCode,
            AuthPort = fccConfig.AuthPort,
            FccPumpAddressMap = fccConfig.FccPumpAddressMap,
            ClientId = fccConfig.ClientId,
            ClientSecret = fccConfig.ClientSecret,
            WebhookSecret = fccConfig.WebhookSecret,
            OAuthTokenEndpoint = fccConfig.OAuthTokenEndpoint,
            AdvatecDevicePort = fccConfig.AdvatecDevicePort,
            AdvatecWebhookToken = fccConfig.AdvatecWebhookToken,
            AdvatecEfdSerialNumber = fccConfig.AdvatecEfdSerialNumber,
            AdvatecCustIdType = fccConfig.AdvatecCustIdType,
            AdvatecPumpMap = fccConfig.AdvatecPumpMap
        };
    }

    private SyncDto BuildSyncDto()
    {
        var section = _configuration.GetSection("EdgeAgentDefaults:Sync");
        return new SyncDto
        {
            CloudBaseUrl = section["CloudBaseUrl"] ?? "https://api.fccmiddleware.com",
            UploadBatchSize = section.GetValue("UploadBatchSize", 100),
            UploadIntervalSeconds = section.GetValue("UploadIntervalSeconds", 30),
            SyncedStatusPollIntervalSeconds = section.GetValue("SyncedStatusPollIntervalSeconds", 60),
            ConfigPollIntervalSeconds = section.GetValue("ConfigPollIntervalSeconds", 300),
            CursorStrategy = section["CursorStrategy"] ?? "FCC_TRANSACTION_ID",
            MaxReplayBackoffSeconds = section.GetValue("MaxReplayBackoffSeconds", 300),
            InitialReplayBackoffSeconds = section.GetValue("InitialReplayBackoffSeconds", 5),
            MaxRecordsPerUploadWindow = section.GetValue("MaxRecordsPerUploadWindow", 5000),
            CertificatePins = section.GetSection("CertificatePins").Get<string[]>() ?? [],
            Environment = section["Environment"]
        };
    }

    private BufferDto BuildBufferDto()
    {
        var section = _configuration.GetSection("EdgeAgentDefaults:Buffer");
        return new BufferDto
        {
            RetentionDays = section.GetValue("RetentionDays", 30),
            StalePendingDays = section.GetValue("StalePendingDays", 3),
            MaxRecords = section.GetValue("MaxRecords", 50000),
            CleanupIntervalHours = section.GetValue("CleanupIntervalHours", 6),
            PersistRawPayloads = section.GetValue("PersistRawPayloads", false)
        };
    }

    private LocalApiDto BuildLocalApiDto()
    {
        var section = _configuration.GetSection("EdgeAgentDefaults:LocalApi");
        return new LocalApiDto
        {
            LocalhostPort = section.GetValue("LocalhostPort", 8080),
            EnableLanApi = section.GetValue("EnableLanApi", false),
            LanBindAddress = null,
            LanAllowCidrs = [],
            LanApiKeyRef = null,
            RateLimitPerMinute = section.GetValue("RateLimitPerMinute", 120)
        };
    }

    private SiteHaDto BuildSiteHaDto(AgentRegistration currentAgent, IReadOnlyList<AgentRegistration> siteAgents)
    {
        var section = _configuration.GetSection("EdgeAgentDefaults:SiteHa");
        var enabled = section.GetValue("Enabled", false);
        var autoFailoverEnabled = enabled && section.GetValue("AutoFailoverEnabled", false);
        var heartbeatIntervalSeconds = section.GetValue("HeartbeatIntervalSeconds", 5);
        var failoverTimeoutSeconds = section.GetValue("FailoverTimeoutSeconds", 30);
        var maxReplicationLagSeconds = section.GetValue("MaxReplicationLagSeconds", 15);
        var peerDiscoveryMode = section["PeerDiscoveryMode"] ?? "HYBRID";
        var allowFailback = section.GetValue("AllowFailback", false);

        var activePeers = siteAgents
            .Where(agent => agent.IsActive && agent.Status == AgentRegistrationStatus.ACTIVE)
            .OrderBy(agent => agent.SiteHaPriority)
            .ThenBy(agent => agent.RegisteredAt)
            .ToList();

        var leader = DetermineLeader(activePeers);
        var currentRole = ResolvePeerRole(currentAgent, leader);
        var leaderEpoch = activePeers.Count == 0
            ? 0
            : Math.Max(1, activePeers.Max(agent => agent.LeaderEpochSeen ?? 0));

        var peerDirectory = activePeers
            .Select(peer => new PeerDirectoryEntryDto
            {
                AgentId = peer.Id,
                DeviceClass = peer.DeviceClass,
                Status = peer.Status.ToString(),
                RoleCapability = peer.RoleCapability,
                Priority = peer.SiteHaPriority,
                CurrentRole = ResolvePeerRole(peer, leader),
                PeerApiBaseUrl = peer.PeerApiBaseUrl,
                PeerApiAdvertisedHost = peer.PeerApiAdvertisedHost,
                PeerApiPort = peer.PeerApiPort,
                PeerApiTlsEnabled = peer.PeerApiTlsEnabled,
                Capabilities = DeserializeCapabilities(peer.CapabilitiesJson),
                AppVersion = peer.AgentVersion,
                LastHeartbeatUtc = peer.LastSeenAt,
                LeaderEpochSeen = peer.LeaderEpochSeen,
                LastReplicationLagSeconds = peer.LastReplicationLagSeconds
            })
            .ToArray();

        return new SiteHaDto
        {
            Enabled = enabled,
            AutoFailoverEnabled = autoFailoverEnabled,
            Priority = currentAgent.SiteHaPriority,
            RoleCapability = currentAgent.RoleCapability,
            CurrentRole = currentRole,
            HeartbeatIntervalSeconds = heartbeatIntervalSeconds,
            FailoverTimeoutSeconds = failoverTimeoutSeconds,
            MaxReplicationLagSeconds = maxReplicationLagSeconds,
            PeerDiscoveryMode = peerDiscoveryMode,
            AllowFailback = allowFailback,
            LeaderAgentId = leader?.Id,
            LeaderEpoch = leaderEpoch,
            LeaderSinceUtc = leader?.RegisteredAt,
            PeerDirectory = peerDirectory
        };
    }

    private static AgentRegistration? DetermineLeader(IReadOnlyList<AgentRegistration> activePeers) =>
        activePeers
            .Where(peer => !string.Equals(peer.RoleCapability, "READ_ONLY", StringComparison.OrdinalIgnoreCase))
            .OrderBy(peer => peer.SiteHaPriority)
            .ThenBy(peer => string.Equals(peer.DeviceClass, "DESKTOP", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(peer => peer.RegisteredAt)
            .FirstOrDefault();

    private static string ResolvePeerRole(AgentRegistration peer, AgentRegistration? leader)
    {
        if (!peer.IsActive || peer.Status != AgentRegistrationStatus.ACTIVE)
        {
            return "OFFLINE";
        }

        if (!string.IsNullOrWhiteSpace(peer.CurrentRole))
        {
            return peer.CurrentRole!;
        }

        if (string.Equals(peer.RoleCapability, "READ_ONLY", StringComparison.OrdinalIgnoreCase))
        {
            return "READ_ONLY";
        }

        return leader?.Id == peer.Id ? "PRIMARY" : "STANDBY_HOT";
    }

    private static string[] DeserializeCapabilities(string? capabilitiesJson)
    {
        if (string.IsNullOrWhiteSpace(capabilitiesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(capabilitiesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private TelemetryDto BuildTelemetryDto()
    {
        var section = _configuration.GetSection("EdgeAgentDefaults:Telemetry");
        return new TelemetryDto
        {
            TelemetryIntervalSeconds = section.GetValue("TelemetryIntervalSeconds", 60),
            LogLevel = section["LogLevel"] ?? "INFO",
            IncludeDiagnosticsLogs = section.GetValue("IncludeDiagnosticsLogs", false),
            MetricsWindowSeconds = section.GetValue("MetricsWindowSeconds", 300)
        };
    }

    private static FiscalizationDto BuildFiscalizationDto(Site site)
    {
        var mode = site.FiscalizationMode;
        return new FiscalizationDto
        {
            Mode = mode.ToString(),
            TaxAuthorityEndpoint = site.TaxAuthorityEndpoint,
            RequireCustomerTaxId = site.RequireCustomerTaxId,
            FiscalReceiptRequired = site.FiscalReceiptRequired || mode == FiscalizationMode.FCC_DIRECT
        };
    }

    private async Task<MappingsDto> BuildMappingsDtoAsync(Domain.Entities.FccConfig fccConfig, CancellationToken cancellationToken)
    {
        var site = fccConfig.Site;
        var adapterKey = fccConfig.FccVendor.ToString();
        var defaultRow = await _db.GetAdapterDefaultConfigAsync(fccConfig.LegalEntityId, adapterKey, cancellationToken);
        var overrideRow = await _db.GetSiteAdapterOverrideAsync(site.Id, adapterKey, cancellationToken);
        var extras = ReadEffectiveAdapterExtras(defaultRow?.ConfigJson, overrideRow?.OverrideJson);
        var pumpNumberOffset = ReadPumpNumberOffset(extras);
        var configuredProductMap = ReadProductCodeMapping(extras);

        var products = new List<ProductMappingDto>();
        var nozzles = new List<NozzleMappingDto>();

        if (configuredProductMap.Count > 0)
        {
            var knownProducts = site.Pumps
                .Where(pump => pump.IsActive)
                .SelectMany(pump => pump.Nozzles.Where(nozzle => nozzle.IsActive))
                .Select(nozzle => nozzle.Product)
                .DistinctBy(product => product.ProductCode)
                .ToDictionary(product => product.ProductCode, product => product.ProductName);

            foreach (var mapping in configuredProductMap)
            {
                products.Add(new ProductMappingDto
                {
                    FccProductCode = mapping.Key,
                    CanonicalProductCode = mapping.Value,
                    DisplayName = knownProducts.TryGetValue(mapping.Value, out var displayName)
                        ? displayName
                        : mapping.Value,
                    Active = true
                });
            }
        }

        foreach (var pump in site.Pumps.Where(p => p.IsActive))
        {
            foreach (var nozzle in pump.Nozzles.Where(n => n.IsActive))
            {
                var product = nozzle.Product;

                // Add product mapping if not already present
                if (!products.Any(p => p.CanonicalProductCode == product.ProductCode))
                {
                    products.Add(new ProductMappingDto
                    {
                        FccProductCode = product.ProductCode,
                        CanonicalProductCode = product.ProductCode,
                        DisplayName = product.ProductName,
                        Active = product.IsActive
                    });
                }

                // Add nozzle mapping with Odoo ↔ FCC number mapping
                nozzles.Add(new NozzleMappingDto
                {
                    OdooPumpNumber = pump.PumpNumber,
                    FccPumpNumber = pump.FccPumpNumber,
                    OdooNozzleNumber = nozzle.OdooNozzleNumber,
                    FccNozzleNumber = nozzle.FccNozzleNumber,
                    ProductCode = product.ProductCode
                });
            }
        }

        return new MappingsDto
        {
            PumpNumberOffset = pumpNumberOffset,
            PriceDecimalPlaces = 2,
            VolumeUnit = "LITRE",
            Products = products.ToArray(),
            Nozzles = nozzles.ToArray()
        };
    }

    private static JsonElement ReadEffectiveAdapterExtras(string? defaultJson, string? overrideJson)
    {
        using var defaultsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(defaultJson) ? "{}" : defaultJson);
        using var overridesDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(overrideJson) ? "{}" : overrideJson);

        var merged = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in defaultsDoc.RootElement.EnumerateObject())
        {
            if (property.Name is "pumpNumberOffset" or "productCodeMapping")
            {
                merged[property.Name] = property.Value.Clone();
            }
        }

        foreach (var property in overridesDoc.RootElement.EnumerateObject())
        {
            if (property.Name is "pumpNumberOffset" or "productCodeMapping")
            {
                merged[property.Name] = property.Value.Clone();
            }
        }

        return JsonSerializer.SerializeToElement(merged);
    }

    private static int ReadPumpNumberOffset(JsonElement extras)
    {
        if (extras.ValueKind == JsonValueKind.Object
            && extras.TryGetProperty("pumpNumberOffset", out var value)
            && value.TryGetInt32(out var offset))
        {
            return offset;
        }

        return 0;
    }

    private static Dictionary<string, string> ReadProductCodeMapping(JsonElement extras)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (extras.ValueKind != JsonValueKind.Object
            || !extras.TryGetProperty("productCodeMapping", out var mapping)
            || mapping.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in mapping.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                result[property.Name] = property.Value.GetString()!;
            }
        }

        return result;
    }

    private RolloutDto BuildRolloutDto()
    {
        var section = _configuration.GetSection("EdgeAgentDefaults:Rollout");
        return new RolloutDto
        {
            MinAgentVersion = section["MinAgentVersion"] ?? "1.0.0",
            MaxAgentVersion = section["MaxAgentVersion"],
            RequiresRestartSections = ["fcc", "sync", "localApi"],
            ConfigTtlHours = section.GetValue("ConfigTtlHours", 24)
        };
    }
}

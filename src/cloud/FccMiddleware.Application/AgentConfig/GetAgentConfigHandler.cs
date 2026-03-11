using FccMiddleware.Application.Common;
using FccMiddleware.Contracts.Config;
using MediatR;
using Microsoft.Extensions.Configuration;

namespace FccMiddleware.Application.AgentConfig;

/// <summary>
/// Builds a full SiteConfig snapshot from DB entities (fcc_configs, sites, legal_entities,
/// pumps, nozzles, products) and returns it to the Edge Agent.
/// Returns 304-equivalent when the client already has the current config version.
/// </summary>
public sealed class GetAgentConfigHandler
    : IRequestHandler<GetAgentConfigQuery, Result<GetAgentConfigResult>>
{
    private readonly IAgentConfigDbContext _db;
    private readonly IConfiguration _configuration;

    public GetAgentConfigHandler(IAgentConfigDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
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

        // Load FccConfig with all related site data
        var fccConfig = await _db.GetFccConfigWithSiteDataAsync(
            request.SiteCode, request.LegalEntityId, cancellationToken);

        if (fccConfig is null)
        {
            return Result<GetAgentConfigResult>.Failure("CONFIG_NOT_FOUND",
                "No active FCC configuration found for this site.");
        }

        // ETag comparison: return 304 if client already has the current version
        if (request.ClientConfigVersion.HasValue
            && request.ClientConfigVersion.Value == fccConfig.ConfigVersion)
        {
            return Result<GetAgentConfigResult>.Success(new GetAgentConfigResult
            {
                NotModified = true,
                ConfigVersion = fccConfig.ConfigVersion
            });
        }

        // Build the full SiteConfig
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
                DeviceId = request.DeviceId.ToString(),
                IsPrimaryAgent = true
            },
            Site = new SiteDto
            {
                IsActive = site.IsActive,
                OperatingModel = site.OperatingModel.ToString(),
                ConnectivityMode = site.ConnectivityMode,
                OdooSiteId = site.OdooSiteId ?? string.Empty,
                CompanyTaxPayerId = site.CompanyTaxPayerId,
                OperatorName = site.OperatorName,
                OperatorTaxPayerId = site.OperatorTaxPayerId
            },
            Fcc = BuildFccDto(fccConfig),
            Sync = BuildSyncDto(),
            Buffer = BuildBufferDto(),
            LocalApi = BuildLocalApiDto(),
            Telemetry = BuildTelemetryDto(),
            Fiscalization = BuildFiscalizationDto(legalEntity),
            Mappings = BuildMappingsDto(site),
            Rollout = BuildRolloutDto()
        };

        return Result<GetAgentConfigResult>.Success(new GetAgentConfigResult
        {
            NotModified = false,
            ConfigVersion = fccConfig.ConfigVersion,
            Config = config
        });
    }

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
            PushSourceIpAllowList = []
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
            MaxRecordsPerUploadWindow = section.GetValue("MaxRecordsPerUploadWindow", 5000)
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

    private static FiscalizationDto BuildFiscalizationDto(Domain.Entities.LegalEntity legalEntity)
    {
        var mode = legalEntity.FiscalizationRequired ? "FCC_DIRECT" : "NONE";
        return new FiscalizationDto
        {
            Mode = mode,
            TaxAuthorityEndpoint = null,
            RequireCustomerTaxId = false,
            FiscalReceiptRequired = legalEntity.FiscalizationRequired
        };
    }

    private static MappingsDto BuildMappingsDto(Domain.Entities.Site site)
    {
        var products = new List<ProductMappingDto>();
        var nozzles = new List<NozzleMappingDto>();

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
            PumpNumberOffset = 0,
            PriceDecimalPlaces = 2,
            VolumeUnit = "LITRES",
            Products = products.ToArray(),
            Nozzles = nozzles.ToArray()
        };
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

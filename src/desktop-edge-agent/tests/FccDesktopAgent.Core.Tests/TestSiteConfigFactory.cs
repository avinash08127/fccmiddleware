namespace FccDesktopAgent.Core.Tests;

internal static class TestSiteConfigFactory
{
    public static SiteConfig Create(
        int configVersion = 1,
        DateTimeOffset? effectiveAt = null,
        Action<SiteConfig>? configure = null)
    {
        var config = new SiteConfig
        {
            SchemaVersion = "1.0",
            ConfigVersion = configVersion,
            ConfigId = Guid.NewGuid(),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            EffectiveAtUtc = effectiveAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            SourceRevision = new SiteConfigSourceRevision(),
            Identity = new SiteConfigIdentity
            {
                DeviceId = Guid.NewGuid().ToString(),
                SiteCode = "SITE-001",
                LegalEntityId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                LegalEntityCode = "LE-001",
                SiteId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                SiteName = "Test Site",
                Timezone = "Africa/Addis_Ababa",
                CurrencyCode = "ETB",
                DeviceClass = "DESKTOP",
                IsPrimaryAgent = false
            },
            Site = new SiteConfigSite
            {
                IsActive = true,
                OperatingModel = "STANDARD",
                SiteUsesPreAuth = false,
                ConnectivityMode = "ONLINE",
                OdooSiteId = "ODOO-1",
                CompanyTaxPayerId = "TAX-1",
                OperatorName = "Operator",
                OperatorTaxPayerId = "OP-TAX"
            },
            Fcc = new SiteConfigFcc
            {
                Enabled = true,
                Vendor = "DOMS",
                ConnectionProtocol = "REST",
                HostAddress = "127.0.0.1",
                Port = 8080,
                CredentialRef = null,
                CredentialRevision = null,
                SecretEnvelope = new SiteConfigSecretEnvelope
                {
                    Format = "PLAINTEXT",
                    Payload = null
                },
                TransactionMode = null,
                IngestionMode = "RELAY",
                PullIntervalSeconds = 30,
                CatchUpPullIntervalSeconds = null,
                HybridCatchUpIntervalSeconds = null,
                HeartbeatIntervalSeconds = 30,
                HeartbeatTimeoutSeconds = 90,
                PushSourceIpAllowList = [],
                JplPort = null,
                FcAccessCode = null,
                DomsCountryCode = null,
                PosVersionId = null,
                ConfiguredPumps = null,
                DppPorts = null,
                ReconnectBackoffMaxSeconds = null,
                SharedSecret = null,
                UsnCode = null,
                AuthPort = null,
                FccPumpAddressMap = null,
                ClientId = null,
                ClientSecret = null,
                WebhookSecret = null,
                OAuthTokenEndpoint = null,
                AdvatecDevicePort = null,
                AdvatecWebhookToken = null,
                AdvatecEfdSerialNumber = null,
                AdvatecCustIdType = null,
                AdvatecPumpMap = null
            },
            Sync = new SiteConfigSync
            {
                CloudBaseUrl = "https://cloud.test",
                UploadIntervalSeconds = 60,
                UploadBatchSize = 50,
                ConfigPollIntervalSeconds = 60,
                SyncedStatusPollIntervalSeconds = 300,
                CursorStrategy = "SEQUENCE",
                MaxReplayBackoffSeconds = 300,
                InitialReplayBackoffSeconds = 5,
                MaxRecordsPerUploadWindow = 500,
                CertificatePins = [],
                Environment = "TEST"
            },
            Buffer = new SiteConfigBuffer
            {
                RetentionDays = 7,
                CleanupIntervalHours = 24,
                MaxRecords = 1000,
                PersistRawPayloads = false,
                StalePendingDays = 3
            },
            LocalApi = new SiteConfigLocalApi
            {
                LocalhostPort = 8585,
                EnableLanApi = false,
                LanBindAddress = null,
                LanAllowCidrs = [],
                LanApiKeyRef = null,
                RateLimitPerMinute = 60
            },
            SiteHa = new SiteConfigSiteHa
            {
                Enabled = false,
                AutoFailoverEnabled = false,
                Priority = 100,
                RoleCapability = "PRIMARY_ELIGIBLE",
                CurrentRole = "PRIMARY",
                HeartbeatIntervalSeconds = 5,
                FailoverTimeoutSeconds = 30,
                MaxReplicationLagSeconds = 15,
                PeerDiscoveryMode = "CLOUD",
                AllowFailback = true,
                LeaderAgentId = null,
                LeaderEpoch = 0,
                LeaderSinceUtc = null,
                PeerDirectory = [],
                PeerApiPort = 8586,
                PeerSharedSecret = null,
                ReplicationEnabled = false,
                ProxyingEnabled = false
            },
            Telemetry = new SiteConfigTelemetry
            {
                TelemetryIntervalSeconds = 300,
                LogLevel = "Information",
                IncludeDiagnosticsLogs = false,
                MetricsWindowSeconds = 300
            },
            Fiscalization = new SiteConfigFiscalization
            {
                Mode = "NONE",
                TaxAuthorityEndpoint = null,
                RequireCustomerTaxId = false,
                FiscalReceiptRequired = false
            },
            Mappings = new SiteConfigMappings
            {
                PumpNumberOffset = 0,
                PriceDecimalPlaces = 2,
                VolumeUnit = "LITRE",
                Products = [],
                Nozzles = []
            },
            Rollout = new SiteConfigRollout
            {
                MinAgentVersion = "1.0.0",
                MaxAgentVersion = null,
                RequiresRestartSections = [],
                ConfigTtlHours = 24
            },
        };

        configure?.Invoke(config);
        return config;
    }
}

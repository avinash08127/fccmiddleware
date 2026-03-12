using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VirtualLab.Domain.Benchmarking;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Infrastructure.FccProfiles;

namespace VirtualLab.Infrastructure.Persistence.Seed;

public sealed class VirtualLabSeedService(
    VirtualLabDbContext dbContext,
    IOptions<BenchmarkSeedProfile> seedProfileOptions) : IVirtualLabSeedService
{
    private readonly BenchmarkSeedProfile seedProfile = seedProfileOptions.Value;

    public async Task SeedAsync(bool resetExisting, CancellationToken cancellationToken = default)
    {
        if (resetExisting)
        {
            await ResetAsync(cancellationToken);
        }

        bool hasEnvironment = await dbContext.LabEnvironments.AnyAsync(cancellationToken);
        if (hasEnvironment)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LabEnvironment environment = new()
        {
            Id = DeterministicGuid("env:default"),
            Key = "default-lab",
            Name = "Default Virtual Lab",
            Description = "Deterministic seeded environment for demos and local development.",
            SettingsJson = """
            {
              "retention": {
                "logRetentionDays": 30,
                "callbackHistoryRetentionDays": 30,
                "transactionRetentionDays": 90,
                "preserveTimelineIntegrity": true
              },
              "backup": {
                "includeRuntimeDataByDefault": true,
                "includeScenarioRunsByDefault": true
              },
              "telemetry": {
                "emitMetrics": true,
                "emitActivities": true
              }
            }
            """,
            SeedVersion = 1,
            DeterministicSeed = seedProfile.ScenarioSeed,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastSeededAtUtc = now,
        };

        List<FccSimulatorProfile> profiles =
        [
            CreateProfile(environment.Id, "doms-like", "DOMS-like profile", "DOMS", SimulatedAuthMode.ApiKey, TransactionDeliveryMode.Hybrid, PreAuthFlowMode.CreateThenAuthorize, now),
            CreateProfile(environment.Id, "generic-create-only", "Generic create-only profile", "GENERIC", SimulatedAuthMode.None, TransactionDeliveryMode.Pull, PreAuthFlowMode.CreateOnly, now),
            CreateProfile(environment.Id, "generic-create-then-authorize", "Generic create-then-authorize profile", "GENERIC", SimulatedAuthMode.BasicAuth, TransactionDeliveryMode.Push, PreAuthFlowMode.CreateThenAuthorize, now),
            CreateProfile(environment.Id, "bulk-push", "Bulk push profile", "GENERIC", SimulatedAuthMode.ApiKey, TransactionDeliveryMode.Push, PreAuthFlowMode.CreateOnly, now),
        ];

        List<Product> products =
        [
            CreateProduct(environment.Id, "ULP", "Unleaded Petrol", "91", "#dc2626", 1.53m, now),
            CreateProduct(environment.Id, "DSL", "Diesel", "D50", "#16a34a", 1.49m, now),
            CreateProduct(environment.Id, "SUP", "Super Unleaded", "95", "#2563eb", 1.61m, now),
        ];

        List<ScenarioDefinition> scenarioDefinitions =
        [
            new()
            {
                Id = DeterministicGuid("scenario:fiscalized-preauth-success"),
                LabEnvironmentId = environment.Id,
                ScenarioKey = "fiscalized-preauth-success",
                Name = "Fiscalized Pre-Auth Success",
                Description = "Deterministic create-then-authorize pre-auth flow with successful push delivery.",
                DeterministicSeed = seedProfile.ScenarioSeed,
                DefinitionJson = """
                {
                  "version": 1,
                  "siteCode": "VL-MW-BT001",
                  "setup": {
                    "resetNozzles": true,
                    "clearActivePreAuth": true,
                    "profileKey": "doms-like",
                    "deliveryMode": "Hybrid",
                    "preAuthMode": "CreateThenAuthorize"
                  },
                  "actions": [
                    { "kind": "preauth", "name": "Create pre-auth", "action": "create", "correlationAlias": "flow", "pumpNumber": 1, "nozzleNumber": 1, "amount": 15000, "expiresInSeconds": 300, "customerName": "Demo Fleet", "customerTaxId": "TAX-123", "customerTaxOffice": "Lilongwe" },
                    { "kind": "preauth", "name": "Authorize pre-auth", "action": "authorize", "correlationAlias": "flow", "pumpNumber": 1, "nozzleNumber": 1, "amount": 15000 },
                    { "kind": "lift", "name": "Lift nozzle", "correlationAlias": "flow", "pumpNumber": 1, "nozzleNumber": 1 },
                    { "kind": "dispense", "name": "Start dispense", "action": "start", "correlationAlias": "flow", "pumpNumber": 1, "nozzleNumber": 1, "targetAmount": 9725.5, "elapsedSeconds": 90 },
                    { "kind": "dispense", "name": "Stop dispense", "action": "stop", "correlationAlias": "flow", "pumpNumber": 1, "nozzleNumber": 1, "elapsedSeconds": 90 },
                    { "kind": "hang", "name": "Hang nozzle", "correlationAlias": "flow", "pumpNumber": 1, "nozzleNumber": 1 },
                    { "kind": "push-transactions", "name": "Push delivery", "correlationAlias": "flow", "targetKey": "demo-callback" }
                  ],
                  "assertions": [
                    { "kind": "preauth-status", "name": "Completed pre-auth exists", "correlationAlias": "flow", "expectedStatus": "Completed", "minimumCount": 1 },
                    { "kind": "transaction-status", "name": "Delivered transaction exists", "correlationAlias": "flow", "expectedStatus": "Delivered", "minimumCount": 1 },
                    { "kind": "callback-attempt-count", "name": "Push attempt recorded", "correlationAlias": "flow", "targetKey": "demo-callback", "minimumCount": 1 }
                  ]
                }
                """,
                ReplaySignature = seedProfile.ComputeReplaySignature(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
            new()
            {
                Id = DeterministicGuid("scenario:create-then-authorize-timeout"),
                LabEnvironmentId = environment.Id,
                ScenarioKey = "create-then-authorize-timeout",
                Name = "Create Then Authorize Timeout",
                Description = "Create a pre-auth, delay, then expire it to simulate an authorization timeout path.",
                DeterministicSeed = seedProfile.ScenarioSeed,
                DefinitionJson = """
                {
                  "version": 1,
                  "siteCode": "VL-MW-BT001",
                  "setup": {
                    "resetNozzles": true,
                    "clearActivePreAuth": true,
                    "profileKey": "doms-like",
                    "deliveryMode": "Hybrid",
                    "preAuthMode": "CreateThenAuthorize"
                  },
                  "actions": [
                    { "kind": "preauth", "name": "Create pre-auth", "action": "create", "correlationAlias": "timeout", "pumpNumber": 1, "nozzleNumber": 1, "amount": 12000, "expiresInSeconds": 2 },
                    { "kind": "delay", "name": "Delay past expiry", "delayMs": 2000 },
                    { "kind": "preauth", "name": "Expire timed-out session", "action": "expire", "correlationAlias": "timeout" }
                  ],
                  "assertions": [
                    { "kind": "preauth-status", "name": "Expired pre-auth exists", "correlationAlias": "timeout", "expectedStatus": "Expired", "minimumCount": 1 }
                  ]
                }
                """,
                ReplaySignature = seedProfile.ComputeReplaySignature(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
            new()
            {
                Id = DeterministicGuid("scenario:bulk-push-duplicate-batch"),
                LabEnvironmentId = environment.Id,
                ScenarioKey = "bulk-push-duplicate-batch",
                Name = "Bulk Push Duplicate Batch",
                Description = "Create a duplicate-injection transaction and verify multiple callback attempts are recorded.",
                DeterministicSeed = seedProfile.ScenarioSeed,
                DefinitionJson = """
                {
                  "version": 1,
                  "siteCode": "VL-MW-BT001",
                  "setup": {
                    "resetNozzles": true,
                    "clearActivePreAuth": true,
                    "profileKey": "bulk-push",
                    "deliveryMode": "Push",
                    "preAuthMode": "CreateOnly"
                  },
                  "actions": [
                    { "kind": "lift", "name": "Lift nozzle", "correlationAlias": "batch", "pumpNumber": 1, "nozzleNumber": 1 },
                    { "kind": "dispense", "name": "Start duplicate dispense", "action": "start", "correlationAlias": "batch", "pumpNumber": 1, "nozzleNumber": 1, "targetAmount": 4500, "elapsedSeconds": 45, "injectDuplicate": true },
                    { "kind": "dispense", "name": "Stop dispense", "action": "stop", "correlationAlias": "batch", "pumpNumber": 1, "nozzleNumber": 1, "elapsedSeconds": 45 },
                    { "kind": "hang", "name": "Hang nozzle", "correlationAlias": "batch", "pumpNumber": 1, "nozzleNumber": 1 },
                    { "kind": "push-transactions", "name": "Push duplicate batch", "correlationAlias": "batch", "targetKey": "demo-callback" }
                  ],
                  "assertions": [
                    { "kind": "transaction-status", "name": "Delivered transaction exists", "correlationAlias": "batch", "expectedStatus": "Delivered", "minimumCount": 1 },
                    { "kind": "callback-attempt-count", "name": "Duplicate callback attempts exist", "correlationAlias": "batch", "targetKey": "demo-callback", "minimumCount": 2 }
                  ]
                }
                """,
                ReplaySignature = seedProfile.ComputeReplaySignature(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
            new()
            {
                Id = DeterministicGuid("scenario:offline-pull-catch-up"),
                LabEnvironmentId = environment.Id,
                ScenarioKey = "offline-pull-catch-up",
                Name = "Offline Pull Catch-Up",
                Description = "Generate a pull-mode transaction, fetch it, and acknowledge it to simulate a catch-up cycle.",
                DeterministicSeed = seedProfile.ScenarioSeed,
                DefinitionJson = """
                {
                  "version": 1,
                  "siteCode": "VL-MW-BT001",
                  "setup": {
                    "resetNozzles": true,
                    "clearActivePreAuth": true,
                    "profileKey": "generic-create-only",
                    "deliveryMode": "Pull",
                    "preAuthMode": "CreateOnly"
                  },
                  "actions": [
                    { "kind": "lift", "name": "Lift nozzle", "correlationAlias": "pull", "pumpNumber": 1, "nozzleNumber": 1 },
                    { "kind": "dispense", "name": "Start dispense", "action": "start", "correlationAlias": "pull", "pumpNumber": 1, "nozzleNumber": 1, "targetAmount": 3600, "elapsedSeconds": 30 },
                    { "kind": "dispense", "name": "Stop dispense", "action": "stop", "correlationAlias": "pull", "pumpNumber": 1, "nozzleNumber": 1, "elapsedSeconds": 30 },
                    { "kind": "hang", "name": "Hang nozzle", "correlationAlias": "pull", "pumpNumber": 1, "nozzleNumber": 1 },
                    { "kind": "pull-transactions", "name": "Pull transaction batch", "limit": 10 },
                    { "kind": "acknowledge-transactions", "name": "Acknowledge pulled batch", "correlationAlias": "pull" }
                  ],
                  "assertions": [
                    { "kind": "transaction-status", "name": "Acknowledged transaction exists", "correlationAlias": "pull", "expectedStatus": "Acknowledged", "minimumCount": 1 },
                    { "kind": "log-count", "name": "Acknowledgement log exists", "correlationAlias": "pull", "category": "TransactionPulled", "eventType": "TransactionAcknowledged", "minimumCount": 1 }
                  ]
                }
                """,
                ReplaySignature = seedProfile.ComputeReplaySignature(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
        ];

        Site site = new()
        {
            Id = DeterministicGuid("site:mw-bt001"),
            LabEnvironmentId = environment.Id,
            ActiveFccSimulatorProfileId = profiles[0].Id,
            SiteCode = "VL-MW-BT001",
            Name = "Blantyre Demo Site",
            TimeZone = "Africa/Blantyre",
            CurrencyCode = "MWK",
            ExternalReference = "demo-site-1",
            InboundAuthMode = SimulatedAuthMode.ApiKey,
            ApiKeyHeaderName = "X-Api-Key",
            ApiKeyValue = "demo-site-key",
            DeliveryMode = TransactionDeliveryMode.Hybrid,
            PreAuthMode = PreAuthFlowMode.CreateThenAuthorize,
            SettingsJson = """
            {
              "defaultCallbackTargetKey": "demo-callback",
              "pullPageSize": 100
            }
            """,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        CallbackTarget callbackTarget = new()
        {
            Id = DeterministicGuid("callback:demo"),
            LabEnvironmentId = environment.Id,
            SiteId = site.Id,
            TargetKey = "demo-callback",
            Name = "Demo Callback Sink",
            CallbackUrl = new Uri("https://example.invalid/virtual-lab/callbacks/demo"),
            AuthMode = SimulatedAuthMode.BasicAuth,
            BasicAuthUsername = "demo",
            BasicAuthPassword = "demo-password",
            CreatedAtUtc = now,
        };

        List<Pump> pumps = [];
        List<Nozzle> nozzles = [];

        for (int pumpNumber = 1; pumpNumber <= 2; pumpNumber++)
        {
            Pump pump = new()
            {
                Id = DeterministicGuid($"pump:{pumpNumber}"),
                SiteId = site.Id,
                PumpNumber = pumpNumber,
                FccPumpNumber = pumpNumber,
                LayoutX = 120 + ((pumpNumber - 1) * 240),
                LayoutY = 120,
                Label = $"Pump {pumpNumber}",
                CreatedAtUtc = now,
            };
            pumps.Add(pump);

            for (int nozzleNumber = 1; nozzleNumber <= 3; nozzleNumber++)
            {
                Product product = products[(nozzleNumber - 1) % products.Count];
                nozzles.Add(new Nozzle
                {
                    Id = DeterministicGuid($"pump:{pumpNumber}:nozzle:{nozzleNumber}"),
                    PumpId = pump.Id,
                    ProductId = product.Id,
                    NozzleNumber = nozzleNumber,
                    FccNozzleNumber = nozzleNumber,
                    Label = $"P{pumpNumber}-N{nozzleNumber}",
                    State = NozzleState.Idle,
                    SimulationStateJson = "{}",
                    UpdatedAtUtc = now,
                });
            }
        }

        ScenarioRun scenarioRun = new()
        {
            Id = DeterministicGuid("scenario-run:default-demo"),
            SiteId = site.Id,
            ScenarioDefinitionId = scenarioDefinitions[0].Id,
            CorrelationId = "corr-default-scenario",
            ReplaySeed = seedProfile.ScenarioSeed,
            ReplaySignature = seedProfile.ComputeReplaySignature(),
            Status = ScenarioRunStatus.Completed,
            InputSnapshotJson = """
            {
              "siteCode": "VL-MW-BT001",
              "profile": "doms-like"
            }
            """,
            ResultSummaryJson = """
            {
              "transactionsGenerated": 1,
              "preAuthSessionsGenerated": 1
            }
            """,
            StartedAtUtc = now.AddSeconds(-5),
            CompletedAtUtc = now,
        };

        PreAuthSession preAuthSession = new()
        {
            Id = DeterministicGuid("preauth:default"),
            SiteId = site.Id,
            PumpId = pumps[0].Id,
            NozzleId = nozzles[0].Id,
            ScenarioRunId = scenarioRun.Id,
            CorrelationId = "corr-default-flow",
            ExternalReference = "preauth-0001",
            Mode = PreAuthFlowMode.CreateThenAuthorize,
            Status = PreAuthSessionStatus.Completed,
            ReservedAmount = 15000m,
            AuthorizedAmount = 15000m,
            FinalAmount = 9725.50m,
            FinalVolume = 42.113m,
            RawRequestJson = """{"amount":15000,"pump":1,"nozzle":1,"correlationId":"corr-default-flow"}""",
            CanonicalRequestJson = """{"siteCode":"VL-MW-BT001","preAuthId":"preauth-0001","correlationId":"corr-default-flow","pumpNumber":1,"nozzleNumber":1,"productCode":"PMS","requestedAmountMinorUnits":1500000,"unitPriceMinorPerLitre":23100,"currencyCode":"MWK","status":"COMPLETED","requestedAt":"2026-03-11T00:00:00Z","expiresAt":"2026-03-11T00:05:00Z"}""",
            RawResponseJson = """{"status":"completed","preauthId":"preauth-0001","correlationId":"corr-default-flow","expiresAtUtc":"2026-03-11T00:05:00Z"}""",
            CanonicalResponseJson = """{"status":"COMPLETED","preAuthId":"preauth-0001","correlationId":"corr-default-flow","expiresAtUtc":"2026-03-11T00:05:00Z","authorizedAmountMinorUnits":1500000,"finalAmountMinorUnits":972550,"finalVolumeMillilitres":42113,"errorCode":null,"failureMessage":null}""",
            TimelineJson = """
            [
              {"event":"create","at":"2026-03-11T00:00:00Z"},
              {"event":"authorize","at":"2026-03-11T00:00:01Z"},
              {"event":"complete","at":"2026-03-11T00:00:05Z"}
            ]
            """,
            CreatedAtUtc = now.AddSeconds(-5),
            AuthorizedAtUtc = now.AddSeconds(-4),
            CompletedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(5),
        };

        SimulatedTransaction transaction = new()
        {
            Id = DeterministicGuid("transaction:default"),
            SiteId = site.Id,
            PumpId = pumps[0].Id,
            NozzleId = nozzles[0].Id,
            ProductId = products[0].Id,
            PreAuthSessionId = preAuthSession.Id,
            ScenarioRunId = scenarioRun.Id,
            CorrelationId = "corr-default-flow",
            ExternalTransactionId = "TX-0001",
            DeliveryMode = TransactionDeliveryMode.Push,
            Status = SimulatedTransactionStatus.Delivered,
            Volume = 42.113m,
            UnitPrice = products[0].UnitPrice,
            TotalAmount = 9725.50m,
            OccurredAtUtc = now.AddSeconds(-1),
            CreatedAtUtc = now.AddSeconds(-1),
            DeliveredAtUtc = now,
            RawPayloadJson = """{"transactionId":"TX-0001","correlationId":"corr-default-flow","siteCode":"VL-MW-BT001","pumpNumber":1,"nozzleNumber":1,"productCode":"PMS","productName":"Premium Motor Spirit","volume":42.113,"amount":9725.50,"unitPrice":231.00,"currencyCode":"MWK","occurredAtUtc":"2026-03-11T00:00:04Z","preAuthId":"preauth-0001"}""",
            CanonicalPayloadJson = """{"fccTransactionId":"TX-0001","correlationId":"corr-default-flow","siteCode":"VL-MW-BT001","pumpNumber":1,"nozzleNumber":1,"productCode":"PMS","volumeMicrolitres":42113000,"amountMinorUnits":972550,"unitPriceMinorPerLitre":23100,"currencyCode":"MWK","startedAt":"2026-03-11T00:00:00Z","completedAt":"2026-03-11T00:00:04Z","fccVendor":"DOMS","status":"PENDING","preAuthId":"preauth-0001","schemaVersion":1,"source":"VirtualLab"}""",
            RawHeadersJson = """{"content-type":"application/json"}""",
            DeliveryCursor = "cursor-0001",
            MetadataJson = """{"seeded":true,"duplicateInjectionEnabled":false,"simulateFailureEnabled":false}""",
            TimelineJson = """
            [
              {"event":"generated","at":"2026-03-11T00:00:04Z"},
              {"event":"delivered","at":"2026-03-11T00:00:05Z"}
            ]
            """,
        };

        CallbackAttempt callbackAttempt = new()
        {
            Id = DeterministicGuid("callback-attempt:default"),
            CallbackTargetId = callbackTarget.Id,
            SimulatedTransactionId = transaction.Id,
            CorrelationId = transaction.CorrelationId,
            AttemptNumber = 1,
            Status = CallbackAttemptStatus.Succeeded,
            ResponseStatusCode = 202,
            RequestUrl = callbackTarget.CallbackUrl.ToString(),
            RequestHeadersJson = """{"authorization":"Basic ZGVtbzpkZW1vLXBhc3N3b3Jk"}""",
            RequestPayloadJson = transaction.RawPayloadJson,
            ResponseHeadersJson = """{"content-type":"application/json"}""",
            ResponsePayloadJson = """{"accepted":true}""",
            RetryCount = 0,
            MaxRetryCount = 3,
            AttemptedAtUtc = now,
            CompletedAtUtc = now,
            NextRetryAtUtc = null,
            AcknowledgedAtUtc = now,
        };

        List<LabEventLog> eventLogs =
        [
            new()
            {
                Id = DeterministicGuid("log:transaction-generated"),
                SiteId = site.Id,
                FccSimulatorProfileId = profiles[0].Id,
                PreAuthSessionId = preAuthSession.Id,
                SimulatedTransactionId = transaction.Id,
                ScenarioRunId = scenarioRun.Id,
                CorrelationId = transaction.CorrelationId,
                Severity = "Information",
                Category = "TransactionGenerated",
                EventType = "TransactionCreated",
                Message = "Seeded demo transaction generated.",
                RawPayloadJson = transaction.RawPayloadJson,
                CanonicalPayloadJson = transaction.CanonicalPayloadJson,
                MetadataJson = """{"seeded":true}""",
                OccurredAtUtc = now.AddSeconds(-1),
            },
            new()
            {
                Id = DeterministicGuid("log:callback-success"),
                SiteId = site.Id,
                FccSimulatorProfileId = profiles[0].Id,
                SimulatedTransactionId = transaction.Id,
                ScenarioRunId = scenarioRun.Id,
                CorrelationId = transaction.CorrelationId,
                Severity = "Information",
                Category = "CallbackAttempt",
                EventType = "CallbackSucceeded",
                Message = "Seeded callback attempt completed successfully.",
                RawPayloadJson = callbackAttempt.RequestPayloadJson,
                CanonicalPayloadJson = transaction.CanonicalPayloadJson,
                MetadataJson = """{"statusCode":202}""",
                OccurredAtUtc = now,
            },
        ];

        dbContext.Add(environment);
        dbContext.AddRange(profiles);
        dbContext.AddRange(products);
        dbContext.Add(site);
        dbContext.Add(callbackTarget);
        dbContext.AddRange(pumps);
        dbContext.AddRange(nozzles);
        dbContext.AddRange(scenarioDefinitions);
        dbContext.Add(scenarioRun);
        dbContext.Add(preAuthSession);
        dbContext.Add(transaction);
        dbContext.Add(callbackAttempt);
        dbContext.AddRange(eventLogs);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ResetAsync(CancellationToken cancellationToken)
    {
        dbContext.CallbackAttempts.RemoveRange(dbContext.CallbackAttempts);
        dbContext.LabEventLogs.RemoveRange(dbContext.LabEventLogs);
        dbContext.SimulatedTransactions.RemoveRange(dbContext.SimulatedTransactions);
        dbContext.PreAuthSessions.RemoveRange(dbContext.PreAuthSessions);
        dbContext.ScenarioRuns.RemoveRange(dbContext.ScenarioRuns);
        dbContext.ScenarioDefinitions.RemoveRange(dbContext.ScenarioDefinitions);
        dbContext.Nozzles.RemoveRange(dbContext.Nozzles);
        dbContext.Pumps.RemoveRange(dbContext.Pumps);
        dbContext.CallbackTargets.RemoveRange(dbContext.CallbackTargets);
        dbContext.Sites.RemoveRange(dbContext.Sites);
        dbContext.Products.RemoveRange(dbContext.Products);
        dbContext.FccSimulatorProfiles.RemoveRange(dbContext.FccSimulatorProfiles);
        dbContext.LabEnvironments.RemoveRange(dbContext.LabEnvironments);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static FccSimulatorProfile CreateProfile(
        Guid environmentId,
        string key,
        string name,
        string vendorFamily,
        SimulatedAuthMode authMode,
        TransactionDeliveryMode deliveryMode,
        PreAuthFlowMode preAuthMode,
        DateTimeOffset now)
    {
        var contract = SeedProfileFactory.Create(authMode, deliveryMode, preAuthMode);

        return new FccSimulatorProfile
        {
            Id = DeterministicGuid($"profile:{key}"),
            LabEnvironmentId = environmentId,
            ProfileKey = key,
            Name = name,
            VendorFamily = vendorFamily,
            AuthMode = authMode,
            DeliveryMode = deliveryMode,
            PreAuthMode = preAuthMode,
            EndpointBasePath = "/fcc",
            EndpointSurfaceJson = System.Text.Json.JsonSerializer.Serialize(contract.EndpointSurface),
            AuthConfigurationJson = System.Text.Json.JsonSerializer.Serialize(contract.Auth),
            CapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(contract.Capabilities),
            RequestTemplatesJson = System.Text.Json.JsonSerializer.Serialize(contract.RequestTemplates),
            ResponseTemplatesJson = System.Text.Json.JsonSerializer.Serialize(contract.ResponseTemplates),
            ValidationRulesJson = System.Text.Json.JsonSerializer.Serialize(contract.ValidationRules),
            FieldMappingsJson = System.Text.Json.JsonSerializer.Serialize(contract.FieldMappings),
            FailureSimulationJson = System.Text.Json.JsonSerializer.Serialize(contract.FailureSimulation),
            ExtensionConfigurationJson = System.Text.Json.JsonSerializer.Serialize(contract.Extensions),
            IsDefault = key == "doms-like",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    private static Product CreateProduct(
        Guid environmentId,
        string code,
        string name,
        string grade,
        string colorHex,
        decimal unitPrice,
        DateTimeOffset now)
    {
        return new Product
        {
            Id = DeterministicGuid($"product:{code}"),
            LabEnvironmentId = environmentId,
            ProductCode = code,
            Name = name,
            Grade = grade,
            ColorHex = colorHex,
            UnitPrice = unitPrice,
            CurrencyCode = "MWK",
            UpdatedAtUtc = now,
        };
    }

    private static Guid DeterministicGuid(string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(bytes);
        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}

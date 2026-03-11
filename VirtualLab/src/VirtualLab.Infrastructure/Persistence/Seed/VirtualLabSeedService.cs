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

        ScenarioDefinition scenarioDefinition = new()
        {
            Id = DeterministicGuid("scenario:default-demo"),
            LabEnvironmentId = environment.Id,
            ScenarioKey = "default-demo",
            Name = "Default Demo Flow",
            Description = "Deterministic lift, pre-auth, and delivery walkthrough.",
            DeterministicSeed = seedProfile.ScenarioSeed,
            DefinitionJson = """
            {
              "steps": [
                "lift-nozzle",
                "create-preauth",
                "authorize-preauth",
                "dispense",
                "push-transaction"
              ]
            }
            """,
            ReplaySignature = seedProfile.ComputeReplaySignature(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

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
                    UpdatedAtUtc = now,
                });
            }
        }

        ScenarioRun scenarioRun = new()
        {
            Id = DeterministicGuid("scenario-run:default-demo"),
            SiteId = site.Id,
            ScenarioDefinitionId = scenarioDefinition.Id,
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
            RawRequestJson = """{"amount":15000,"pump":1,"nozzle":1}""",
            CanonicalRequestJson = """{"reservedAmount":15000,"pumpNumber":1,"nozzleNumber":1}""",
            RawResponseJson = """{"status":"authorized","authorizationId":"AUTH-0001"}""",
            CanonicalResponseJson = """{"status":"authorized","externalReference":"preauth-0001"}""",
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
            RawPayloadJson = """{"siteCode":"VL-MW-BT001","volume":42.113,"amount":9725.50}""",
            CanonicalPayloadJson = """{"site":"VL-MW-BT001","totalAmount":9725.50,"volume":42.113}""",
            RawHeadersJson = """{"content-type":"application/json"}""",
            DeliveryCursor = "cursor-0001",
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
            RequestHeadersJson = """{"authorization":"Basic ZGVtbzpkZW1vLXBhc3N3b3Jk"}""",
            RequestPayloadJson = transaction.RawPayloadJson,
            ResponseHeadersJson = """{"content-type":"application/json"}""",
            ResponsePayloadJson = """{"accepted":true}""",
            AttemptedAtUtc = now,
            CompletedAtUtc = now,
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
        dbContext.Add(scenarioDefinition);
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

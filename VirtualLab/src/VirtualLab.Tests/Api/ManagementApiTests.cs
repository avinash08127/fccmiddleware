using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;

namespace VirtualLab.Tests.Api;

public sealed class ManagementApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task CreateSiteRejectsIncompatibleProfileWithActionableValidation()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        (Guid environmentId, Guid profileId) = await factory.WithDbContextAsync(async dbContext =>
        {
            Guid envId = await dbContext.LabEnvironments.Select(x => x.Id).SingleAsync();
            Guid incompatibleProfileId = await dbContext.FccSimulatorProfiles
                .Where(x => x.ProfileKey == "generic-create-only")
                .Select(x => x.Id)
                .SingleAsync();
            return (envId, incompatibleProfileId);
        });

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/sites",
            new
            {
                labEnvironmentId = environmentId,
                activeFccSimulatorProfileId = profileId,
                siteCode = "VL-BAD-001",
                name = "Bad Compatibility Site",
                timeZone = "UTC",
                currencyCode = "USD",
                deliveryMode = TransactionDeliveryMode.Push,
                preAuthMode = PreAuthFlowMode.CreateThenAuthorize,
                inboundAuthMode = SimulatedAuthMode.None,
                isActive = true,
                settings = new
                {
                    isTemplate = false,
                    defaultCallbackTargetKey = "",
                    pullPageSize = 100,
                    fiscalization = new
                    {
                        mode = "NONE",
                        requireCustomerTaxId = false,
                        fiscalReceiptRequired = false,
                        taxAuthorityName = "",
                        taxAuthorityEndpoint = "",
                    },
                },
            },
            JsonOptions);

        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("delivery_mode_incompatible", body, StringComparison.Ordinal);
        Assert.Contains("preauth_mode_incompatible", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateSiteCopiesForecourtAndMarksTemplate()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        Guid sourceSiteId = await factory.WithDbContextAsync(async dbContext =>
            await dbContext.Sites.Where(x => x.SiteCode == "VL-MW-BT001").Select(x => x.Id).SingleAsync());

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/sites/{sourceSiteId}/duplicate",
            new
            {
                siteCode = "VL-MW-TEMPLATE-01",
                name = "Template Copy",
                externalReference = "template-copy",
                copyForecourt = true,
                copyCallbackTargets = true,
                markAsTemplate = true,
                activate = false,
            },
            JsonOptions);

        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Equal("VL-MW-TEMPLATE-01", root.GetProperty("siteCode").GetString());
            Assert.False(root.GetProperty("isActive").GetBoolean());
            Assert.True(root.GetProperty("settings").GetProperty("isTemplate").GetBoolean());
            Assert.Equal(2, root.GetProperty("forecourt").GetProperty("pumpCount").GetInt32());
            Assert.Equal(6, root.GetProperty("forecourt").GetProperty("nozzleCount").GetInt32());
            Assert.True(root.GetProperty("callbackTargets").GetArrayLength() >= 1);

        await factory.WithDbContextAsync(async dbContext =>
        {
            Site duplicated = await dbContext.Sites.SingleAsync(x => x.SiteCode == "VL-MW-TEMPLATE-01");
            int pumpCount = await dbContext.Pumps.CountAsync(x => x.SiteId == duplicated.Id);
            int nozzleCount = await dbContext.Nozzles.CountAsync(x => x.Pump.SiteId == duplicated.Id);
            int callbackTargetCount = await dbContext.CallbackTargets.CountAsync(x => x.SiteId == duplicated.Id);

            Assert.False(duplicated.IsActive);
            Assert.Contains("\"isTemplate\": true", duplicated.SettingsJson, StringComparison.Ordinal);
            Assert.Equal(2, pumpCount);
            Assert.Equal(6, nozzleCount);
            Assert.True(callbackTargetCount >= 1);
        });
    }

    [Fact]
    public async Task ProductCrudAndForecourtSaveSupportProductReassignment()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        (Guid environmentId, Guid siteId) = await factory.WithDbContextAsync(async dbContext =>
        {
            Guid envId = await dbContext.LabEnvironments.Select(x => x.Id).SingleAsync();
            Guid existingSiteId = await dbContext.Sites.Where(x => x.SiteCode == "VL-MW-BT001").Select(x => x.Id).SingleAsync();
            return (envId, existingSiteId);
        });

        using HttpResponseMessage createProductResponse = await client.PostAsJsonAsync(
            "/api/products",
            new
            {
                labEnvironmentId = environmentId,
                productCode = "E85",
                name = "Bio Ethanol",
                grade = "E85",
                colorHex = "#F59E0B",
                unitPrice = 1.77m,
                currencyCode = "MWK",
                isActive = true,
            },
            JsonOptions);

        string createProductBody = await createProductResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, createProductResponse.StatusCode);

        using JsonDocument productDocument = JsonDocument.Parse(createProductBody);
        Guid productId = productDocument.RootElement.GetProperty("id").GetGuid();

        using HttpResponseMessage forecourtResponse = await client.GetAsync($"/api/sites/{siteId}/forecourt");
        string forecourtBody = await forecourtResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, forecourtResponse.StatusCode);

        using JsonDocument forecourtDocument = JsonDocument.Parse(forecourtBody);
        JsonElement siteForecourt = forecourtDocument.RootElement;
        JsonElement[] pumps = siteForecourt.GetProperty("pumps").EnumerateArray().ToArray();

        var payload = new
        {
            pumps = pumps.Select((pump, pumpIndex) => new
            {
                id = pump.GetProperty("id").GetGuid(),
                pumpNumber = pump.GetProperty("pumpNumber").GetInt32(),
                fccPumpNumber = pump.GetProperty("fccPumpNumber").GetInt32(),
                layoutX = pumpIndex == 0 ? 480 : pump.GetProperty("layoutX").GetInt32(),
                layoutY = pumpIndex == 0 ? 260 : pump.GetProperty("layoutY").GetInt32(),
                label = pump.GetProperty("label").GetString(),
                isActive = pump.GetProperty("isActive").GetBoolean(),
                nozzles = pump.GetProperty("nozzles").EnumerateArray().Select((nozzle, nozzleIndex) => new
                {
                    id = nozzle.GetProperty("id").GetGuid(),
                    productId = pumpIndex == 0 && nozzleIndex == 0 ? productId : nozzle.GetProperty("productId").GetGuid(),
                    nozzleNumber = nozzle.GetProperty("nozzleNumber").GetInt32(),
                    fccNozzleNumber = nozzle.GetProperty("fccNozzleNumber").GetInt32(),
                    label = nozzle.GetProperty("label").GetString(),
                    isActive = nozzle.GetProperty("isActive").GetBoolean(),
                }).ToArray(),
            }).ToArray(),
        };

        using HttpResponseMessage saveForecourtResponse = await client.PutAsJsonAsync(
            $"/api/sites/{siteId}/forecourt",
            payload,
            JsonOptions);

        string saveForecourtBody = await saveForecourtResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, saveForecourtResponse.StatusCode);
        Assert.Contains("\"productCode\":\"E85\"", saveForecourtBody, StringComparison.Ordinal);
        Assert.Contains("\"layoutX\":480", saveForecourtBody, StringComparison.Ordinal);
        Assert.Contains("\"layoutY\":260", saveForecourtBody, StringComparison.Ordinal);

        using HttpResponseMessage archiveProductResponse = await client.DeleteAsync($"/api/products/{productId}");
        string archiveProductBody = await archiveProductResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, archiveProductResponse.StatusCode);
        Assert.Contains("product_in_use", archiveProductBody, StringComparison.Ordinal);

        using HttpResponseMessage reloadResponse = await client.GetAsync($"/api/sites/{siteId}/forecourt");
        string reloadBody = await reloadResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, reloadResponse.StatusCode);
        Assert.Contains("\"layoutX\":480", reloadBody, StringComparison.Ordinal);
        Assert.Contains("\"layoutY\":260", reloadBody, StringComparison.Ordinal);
        Assert.Contains("\"productCode\":\"E85\"", reloadBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SiteResetClearsSimulationStateAndSiteSeedRecreatesDemoData()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        Guid siteId = await factory.WithDbContextAsync(async dbContext =>
            await dbContext.Sites.Where(x => x.SiteCode == "VL-MW-BT001").Select(x => x.Id).SingleAsync());

        using HttpResponseMessage resetResponse = await client.PostAsync($"/api/sites/{siteId}/reset", null);
        string resetBody = await resetResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        using (JsonDocument document = JsonDocument.Parse(resetBody))
        {
            Assert.True(document.RootElement.GetProperty("transactionsRemoved").GetInt32() >= 1);
            Assert.True(document.RootElement.GetProperty("nozzlesReset").GetInt32() >= 1);
        }

        await factory.WithDbContextAsync(async dbContext =>
        {
            Assert.Equal(0, await dbContext.SimulatedTransactions.CountAsync(x => x.SiteId == siteId));
            Assert.Equal(0, await dbContext.PreAuthSessions.CountAsync(x => x.SiteId == siteId));
            Assert.All(
                await dbContext.Nozzles.Where(x => x.Pump.SiteId == siteId).ToListAsync(),
                nozzle => Assert.Equal(NozzleState.Idle, nozzle.State));
        });

        using HttpResponseMessage seedResponse = await client.PostAsJsonAsync(
            $"/api/sites/{siteId}/seed",
            new
            {
                resetBeforeSeed = false,
                includeCompletedPreAuth = true,
            },
            JsonOptions);

        string seedBody = await seedResponse.Content.ReadAsStringAsync();
        Assert.True(seedResponse.StatusCode == HttpStatusCode.OK, seedBody);
        using (JsonDocument document = JsonDocument.Parse(seedBody))
        {
            Assert.Equal(1, document.RootElement.GetProperty("transactionsCreated").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("preAuthSessionsCreated").GetInt32());
        }

        await factory.WithDbContextAsync(async dbContext =>
        {
            Assert.Equal(1, await dbContext.SimulatedTransactions.CountAsync(x => x.SiteId == siteId));
            Assert.Equal(1, await dbContext.PreAuthSessions.CountAsync(x => x.SiteId == siteId));
        });
    }

    [Fact]
    public async Task ProfileArchiveRejectsActiveSiteAssignment()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        Guid profileId = await factory.WithDbContextAsync(async dbContext =>
            await dbContext.Sites
                .Where(x => x.SiteCode == "VL-MW-BT001")
                .Select(x => x.ActiveFccSimulatorProfileId)
                .SingleAsync());

        using HttpResponseMessage response = await client.DeleteAsync($"/api/fcc-profiles/{profileId}");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("profile_in_use", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LabEnvironmentSettingsAndExportImportRoundTripWork()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage updateResponse = await client.PutAsJsonAsync(
            "/api/lab-environment",
            new
            {
                name = "Default Virtual Lab",
                description = "Updated lifecycle controls for backup and pruning tests.",
                settings = new
                {
                    retention = new
                    {
                        logRetentionDays = 14,
                        callbackHistoryRetentionDays = 21,
                        transactionRetentionDays = 45,
                        preserveTimelineIntegrity = true,
                    },
                    backup = new
                    {
                        includeRuntimeDataByDefault = true,
                        includeScenarioRunsByDefault = true,
                    },
                    telemetry = new
                    {
                        emitMetrics = true,
                        emitActivities = true,
                    },
                },
            },
            JsonOptions);

        string updateBody = await updateResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.Contains("\"logRetentionDays\":14", updateBody, StringComparison.Ordinal);
        Assert.Contains("\"category\":\"AuthFailure\"", updateBody, StringComparison.Ordinal);

        using HttpResponseMessage exportResponse = await client.GetAsync("/api/lab-environment/export?includeRuntimeData=true");
        string exportBody = await exportResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        Assert.Contains("\"includesRuntimeData\":true", exportBody, StringComparison.Ordinal);

        using JsonDocument exportDocument = JsonDocument.Parse(exportBody);
        Assert.True(exportDocument.RootElement.GetProperty("transactions").GetArrayLength() >= 1);

        string settingsJson = exportDocument.RootElement.GetProperty("environment").GetProperty("settingsJson").GetString()!;
        using JsonDocument settingsDocument = JsonDocument.Parse(settingsJson);
        Assert.Equal(14, settingsDocument.RootElement.GetProperty("retention").GetProperty("logRetentionDays").GetInt32());

        using HttpResponseMessage importResponse = await client.PostAsJsonAsync(
            "/api/lab-environment/import",
            new
            {
                replaceExisting = true,
                package = JsonSerializer.Deserialize<object>(exportBody),
            },
            JsonOptions);

        string importBody = await importResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
        Assert.Contains("\"replaceExisting\":true", importBody, StringComparison.Ordinal);
        Assert.Contains("\"transactionCount\":", importBody, StringComparison.Ordinal);

        await factory.WithDbContextAsync(async dbContext =>
        {
            LabEnvironment environment = await dbContext.LabEnvironments.SingleAsync();
            using JsonDocument persistedSettings = JsonDocument.Parse(environment.SettingsJson);
            Assert.Equal(14, persistedSettings.RootElement.GetProperty("retention").GetProperty("logRetentionDays").GetInt32());
            Assert.True(await dbContext.ScenarioDefinitions.AnyAsync());
            Assert.True(await dbContext.SimulatedTransactions.AnyAsync());
        });
    }

    [Fact]
    public async Task LabEnvironmentPruneRemovesOldRuntimeDataAndPreservesScenarioHistory()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        Guid siteId = Guid.Empty;
        Guid oldTransactionId = Guid.Empty;
        Guid oldPreAuthId = Guid.Empty;
        Guid oldAttemptId = Guid.Empty;
        Guid oldLogId = Guid.Empty;
        Guid scenarioTransactionId = Guid.Empty;
        Guid scenarioAttemptId = Guid.Empty;
        Guid scenarioLogId = Guid.Empty;
        DateTimeOffset oldTimestamp = DateTimeOffset.UtcNow.AddDays(-180);

        await factory.WithDbContextAsync(async dbContext =>
        {
            Site site = await dbContext.Sites
                .Include(x => x.Pumps)
                    .ThenInclude(x => x.Nozzles)
                .Include(x => x.CallbackTargets)
                .SingleAsync(x => x.SiteCode == "VL-MW-BT001");

            Product product = await dbContext.Products.OrderBy(x => x.ProductCode).FirstAsync();
            CallbackTarget callbackTarget = site.CallbackTargets.First();
            Pump pump = site.Pumps.OrderBy(x => x.PumpNumber).First();
            Nozzle nozzle = pump.Nozzles.OrderBy(x => x.NozzleNumber).First();

            siteId = site.Id;

            PreAuthSession oldPreAuth = new()
            {
                Id = Guid.NewGuid(),
                SiteId = site.Id,
                PumpId = pump.Id,
                NozzleId = nozzle.Id,
                CorrelationId = "corr-prune-old",
                ExternalReference = "preauth-prune-old",
                Mode = PreAuthFlowMode.CreateThenAuthorize,
                Status = PreAuthSessionStatus.Completed,
                ReservedAmount = 500m,
                AuthorizedAmount = 500m,
                FinalAmount = 480m,
                FinalVolume = 12.5m,
                CreatedAtUtc = oldTimestamp,
                AuthorizedAtUtc = oldTimestamp,
                CompletedAtUtc = oldTimestamp,
            };

            SimulatedTransaction oldTransaction = new()
            {
                Id = Guid.NewGuid(),
                SiteId = site.Id,
                PumpId = pump.Id,
                NozzleId = nozzle.Id,
                ProductId = product.Id,
                PreAuthSessionId = oldPreAuth.Id,
                CorrelationId = "corr-prune-old",
                ExternalTransactionId = "TX-PRUNE-OLD",
                DeliveryMode = TransactionDeliveryMode.Push,
                Status = SimulatedTransactionStatus.Acknowledged,
                Volume = 12.5m,
                UnitPrice = product.UnitPrice,
                TotalAmount = 480m,
                OccurredAtUtc = oldTimestamp,
                CreatedAtUtc = oldTimestamp,
                DeliveredAtUtc = oldTimestamp,
                RawPayloadJson = """{"transactionId":"TX-PRUNE-OLD"}""",
                CanonicalPayloadJson = """{"fccTransactionId":"TX-PRUNE-OLD"}""",
                DeliveryCursor = "cursor-old",
            };

            CallbackAttempt oldAttempt = new()
            {
                Id = Guid.NewGuid(),
                CallbackTargetId = callbackTarget.Id,
                SimulatedTransactionId = oldTransaction.Id,
                CorrelationId = oldTransaction.CorrelationId,
                AttemptNumber = 1,
                Status = CallbackAttemptStatus.Succeeded,
                ResponseStatusCode = 202,
                RequestUrl = callbackTarget.CallbackUrl.ToString(),
                AttemptedAtUtc = oldTimestamp,
                CompletedAtUtc = oldTimestamp,
                AcknowledgedAtUtc = oldTimestamp,
            };

            LabEventLog oldLog = new()
            {
                Id = Guid.NewGuid(),
                SiteId = site.Id,
                FccSimulatorProfileId = site.ActiveFccSimulatorProfileId,
                PreAuthSessionId = oldPreAuth.Id,
                SimulatedTransactionId = oldTransaction.Id,
                CorrelationId = oldTransaction.CorrelationId,
                Severity = "information",
                Category = "transactionpushed",
                EventType = "TransactionPushed",
                Message = "Old non-scenario transaction should be pruned.",
                OccurredAtUtc = oldTimestamp,
            };

            SimulatedTransaction scenarioTransaction = await dbContext.SimulatedTransactions
                .Where(x => x.ScenarioRunId != null)
                .OrderBy(x => x.OccurredAtUtc)
                .FirstAsync();
            scenarioTransaction.OccurredAtUtc = oldTimestamp;
            scenarioTransaction.CreatedAtUtc = oldTimestamp;
            scenarioTransaction.DeliveredAtUtc = oldTimestamp;

            CallbackAttempt scenarioAttempt = await dbContext.CallbackAttempts
                .Where(x => x.SimulatedTransaction.ScenarioRunId != null)
                .OrderBy(x => x.AttemptedAtUtc)
                .FirstAsync();
            scenarioAttempt.AttemptedAtUtc = oldTimestamp;
            scenarioAttempt.CompletedAtUtc = oldTimestamp;
            scenarioAttempt.AcknowledgedAtUtc = oldTimestamp;

            LabEventLog scenarioLog = await dbContext.LabEventLogs
                .Where(x => x.ScenarioRunId != null && x.SimulatedTransactionId != null)
                .OrderBy(x => x.OccurredAtUtc)
                .FirstAsync();
            scenarioLog.OccurredAtUtc = oldTimestamp;

            dbContext.PreAuthSessions.Add(oldPreAuth);
            dbContext.SimulatedTransactions.Add(oldTransaction);
            dbContext.CallbackAttempts.Add(oldAttempt);
            dbContext.LabEventLogs.Add(oldLog);

            await dbContext.SaveChangesAsync();

            oldTransactionId = oldTransaction.Id;
            oldPreAuthId = oldPreAuth.Id;
            oldAttemptId = oldAttempt.Id;
            oldLogId = oldLog.Id;
            scenarioTransactionId = scenarioTransaction.Id;
            scenarioAttemptId = scenarioAttempt.Id;
            scenarioLogId = scenarioLog.Id;
        });

        using HttpResponseMessage pruneResponse = await client.PostAsJsonAsync(
            "/api/lab-environment/prune",
            new
            {
                dryRun = false,
                logRetentionDays = 30,
                callbackHistoryRetentionDays = 30,
                transactionRetentionDays = 30,
                preserveTimelineIntegrity = true,
            },
            JsonOptions);

        string pruneBody = await pruneResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, pruneResponse.StatusCode);
        Assert.Contains("\"transactionsRemoved\":1", pruneBody, StringComparison.Ordinal);
        Assert.Contains("\"callbackAttemptsRemoved\":1", pruneBody, StringComparison.Ordinal);
        Assert.Contains("\"preAuthSessionsRemoved\":1", pruneBody, StringComparison.Ordinal);

        await factory.WithDbContextAsync(async dbContext =>
        {
            Assert.False(await dbContext.SimulatedTransactions.AnyAsync(x => x.Id == oldTransactionId));
            Assert.False(await dbContext.PreAuthSessions.AnyAsync(x => x.Id == oldPreAuthId));
            Assert.False(await dbContext.CallbackAttempts.AnyAsync(x => x.Id == oldAttemptId));
            Assert.False(await dbContext.LabEventLogs.AnyAsync(x => x.Id == oldLogId));

            Assert.True(await dbContext.SimulatedTransactions.AnyAsync(x => x.Id == scenarioTransactionId));
            Assert.True(await dbContext.CallbackAttempts.AnyAsync(x => x.Id == scenarioAttemptId));
            Assert.True(await dbContext.LabEventLogs.AnyAsync(x => x.Id == scenarioLogId));
            Assert.True(await dbContext.ScenarioRuns.AnyAsync(x => x.SiteId == siteId));
        });
    }
}

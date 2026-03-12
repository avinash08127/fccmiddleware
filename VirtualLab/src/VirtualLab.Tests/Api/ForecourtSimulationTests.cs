using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;

namespace VirtualLab.Tests.Api;

public sealed class ForecourtSimulationTests
{
    [Fact]
    public async Task LiftStartStopHangFlowGeneratesInspectableTransaction()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        ForecourtFixture fixture = await ConfigureSiteAsync(factory, "doms-like");

        using HttpResponseMessage liftResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/lift",
            """{"correlationId":"corr-normal-flow"}""");
        string liftBody = await liftResponse.Content.ReadAsStringAsync();

        using HttpResponseMessage startResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/dispense",
            """{"action":"start","correlationId":"corr-normal-flow","flowRateLitresPerMinute":30,"targetVolume":10}""");
        string startBody = await startResponse.Content.ReadAsStringAsync();

        using HttpResponseMessage stopResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/dispense",
            """{"action":"stop","correlationId":"corr-normal-flow","elapsedSeconds":20}""");
        string stopBody = await stopResponse.Content.ReadAsStringAsync();

        using HttpResponseMessage hangResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/hang",
            """{"correlationId":"corr-normal-flow"}""");
        string hangBody = await hangResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, liftResponse.StatusCode);
        Assert.Equal("Lifted", ReadNozzleState(liftBody));
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        Assert.Equal("Dispensing", ReadNozzleState(startBody));
        Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);
        Assert.Equal("Lifted", ReadNozzleState(stopBody));
        Assert.Equal(HttpStatusCode.OK, hangResponse.StatusCode);
        Assert.Equal("Hung", ReadNozzleState(hangBody));
        Assert.True(ReadTransactionGenerated(hangBody));

        using HttpResponseMessage transactionsResponse = await client.GetAsync("/api/transactions?correlationId=corr-normal-flow");
        string transactionsBody = await transactionsResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, transactionsResponse.StatusCode);
        using (JsonDocument document = JsonDocument.Parse(transactionsBody))
        {
            JsonElement transaction = document.RootElement.EnumerateArray().Single();
            Assert.Equal(1, transaction.GetProperty("pumpNumber").GetInt32());
            Assert.Equal(1, transaction.GetProperty("nozzleNumber").GetInt32());
            Assert.Contains("\"siteCode\": \"VL-MW-BT001\"", transaction.GetProperty("rawPayloadJson").GetString(), StringComparison.Ordinal);
            Assert.Contains("\"fccTransactionId\"", transaction.GetProperty("canonicalPayloadJson").GetString(), StringComparison.Ordinal);
            Assert.Contains("TransactionGenerated", transaction.GetProperty("timelineJson").GetString(), StringComparison.Ordinal);
            Assert.Contains("\"flowRateLitresPerMinute\": 30", transaction.GetProperty("metadataJson").GetString(), StringComparison.Ordinal);
            Assert.Equal("Passed", transaction.GetProperty("contractValidation").GetProperty("outcome").GetString());
            Assert.True(transaction.GetProperty("contractValidation").GetProperty("matchedCount").GetInt32() >= 6);
        }

        await factory.WithDbContextAsync(async dbContext =>
        {
            Nozzle nozzle = await dbContext.Nozzles.SingleAsync(x => x.Id == fixture.NozzleId);
            SimulatedTransaction transaction = await dbContext.SimulatedTransactions.SingleAsync(x => x.CorrelationId == "corr-normal-flow");

            Assert.Equal(NozzleState.Hung, nozzle.State);
            Assert.Equal(10.000m, transaction.Volume);
            Assert.True(await dbContext.LabEventLogs.AnyAsync(x => x.CorrelationId == "corr-normal-flow" && x.Category == "StateTransition"));
            Assert.True(await dbContext.LabEventLogs.AnyAsync(x => x.CorrelationId == "corr-normal-flow" && x.Category == "TransactionGenerated"));
            Assert.True(await dbContext.CallbackAttempts.AnyAsync(x => x.SimulatedTransactionId == transaction.Id));
        });
    }

    [Fact]
    public async Task AuthorizedNozzleFlowCompletesLinkedPreAuthSession()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        ForecourtFixture fixture = await ConfigureSiteAsync(factory, "doms-like");

        using HttpResponseMessage createResponse = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/preauth/create",
            """{"amount":15000,"pump":1,"nozzle":1,"correlationId":"corr-preauth-nozzle"}""");
        string createBody = await createResponse.Content.ReadAsStringAsync();
        string preAuthId = ReadRequiredString(createBody, "preauthId");

        using HttpResponseMessage authorizeResponse = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/preauth/authorize",
            $$"""{"preauthId":"{{preAuthId}}","amount":15000,"correlationId":"corr-preauth-nozzle"}""");

        Assert.Equal(HttpStatusCode.OK, authorizeResponse.StatusCode);

        using HttpResponseMessage liftResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/lift",
            "{}");
        string liftBody = await liftResponse.Content.ReadAsStringAsync();

        using HttpResponseMessage startResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/dispense",
            """{"action":"start","elapsedSeconds":12,"targetAmount":5000}""");

        using HttpResponseMessage stopResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/dispense",
            """{"action":"stop","elapsedSeconds":12}""");

        using HttpResponseMessage hangResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/hang",
            "{}");

        Assert.Equal(HttpStatusCode.OK, liftResponse.StatusCode);
        Assert.Equal("Authorized", ReadNozzleState(liftBody));
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, hangResponse.StatusCode);

        await factory.WithDbContextAsync(async dbContext =>
        {
            PreAuthSession session = await dbContext.PreAuthSessions.SingleAsync(x => x.CorrelationId == "corr-preauth-nozzle");
            SimulatedTransaction transaction = await dbContext.SimulatedTransactions.SingleAsync(x => x.CorrelationId == "corr-preauth-nozzle");

            Assert.Equal(PreAuthSessionStatus.Completed, session.Status);
            Assert.Equal(session.Id, transaction.PreAuthSessionId);
            Assert.Contains("DispenseCompleted", session.TimelineJson, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task DuplicateInjectionUsesSingleTransactionRowAcrossPushAndPull()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        ForecourtFixture fixture = await ConfigureSiteAsync(factory, "doms-like");

        await RunNormalFlowAsync(
            client,
            fixture,
            correlationId: "corr-duplicate-flow",
            dispenseStartJson: """{"action":"start","correlationId":"corr-duplicate-flow","flowRateLitresPerMinute":24,"targetVolume":8,"injectDuplicate":true}""",
            dispenseStopJson: """{"action":"stop","correlationId":"corr-duplicate-flow","elapsedSeconds":20}""");

        using HttpResponseMessage pullResponse = await client.GetAsync("/fcc/VL-MW-BT001/transactions?limit=10");
        string pullBody = await pullResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, pullResponse.StatusCode);
        using JsonDocument pullDocument = JsonDocument.Parse(pullBody);
        JsonElement[] pulledTransactions = pullDocument.RootElement.GetProperty("transactions").EnumerateArray().ToArray();
        string externalTransactionId = pulledTransactions[0].GetProperty("transactionId").GetString()
            ?? throw new InvalidOperationException("Missing transactionId.");

        Assert.True(pulledTransactions.Length >= 2);
        Assert.All(pulledTransactions.Take(2), entry => Assert.Equal(externalTransactionId, entry.GetProperty("transactionId").GetString()));

        using HttpResponseMessage ackResponse = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/transactions/ack",
            $$"""{"transactionIds":["{{externalTransactionId}}"],"correlationId":"corr-duplicate-ack"}""");
        string ackBody = await ackResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, ackResponse.StatusCode);
        Assert.Equal(1, ReadRequiredInt(ackBody, "acknowledged"));

        await factory.WithDbContextAsync(async dbContext =>
        {
            SimulatedTransaction transaction = await dbContext.SimulatedTransactions.SingleAsync(x => x.CorrelationId == "corr-duplicate-flow");
            int transactionCount = await dbContext.SimulatedTransactions.CountAsync(x => x.CorrelationId == "corr-duplicate-flow");
            int callbackAttempts = await dbContext.CallbackAttempts.CountAsync(x => x.SimulatedTransactionId == transaction.Id);

            Assert.Equal(1, transactionCount);
            Assert.Equal(SimulatedTransactionStatus.Acknowledged, transaction.Status);
            Assert.True(callbackAttempts >= 2);
            Assert.True(await dbContext.LabEventLogs.AnyAsync(x => x.EventType == "DuplicatePushInjected" && x.CorrelationId == "corr-duplicate-flow"));
            Assert.True(await dbContext.LabEventLogs.AnyAsync(x => x.EventType == "DuplicatePullInjected" && x.CorrelationId == "corr-duplicate-flow"));
        });
    }

    [Fact]
    public async Task FailureInjectionLeavesTransactionRecoverableForRetry()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        ForecourtFixture fixture = await ConfigureSiteAsync(factory, "doms-like");

        await RunNormalFlowAsync(
            client,
            fixture,
            correlationId: "corr-failure-flow",
            dispenseStartJson: """{"action":"start","correlationId":"corr-failure-flow","flowRateLitresPerMinute":20,"targetAmount":4000,"simulateFailure":true,"failureMessage":"forced push failure"}""",
            dispenseStopJson: """{"action":"stop","correlationId":"corr-failure-flow","elapsedSeconds":18}""");

        await factory.WithDbContextAsync(async dbContext =>
        {
            SimulatedTransaction failedTransaction = await dbContext.SimulatedTransactions.SingleAsync(x => x.CorrelationId == "corr-failure-flow");
            CallbackAttempt pendingAttempt = await dbContext.CallbackAttempts.SingleAsync(x => x.SimulatedTransactionId == failedTransaction.Id);

            Assert.Equal(SimulatedTransactionStatus.Failed, failedTransaction.Status);
            Assert.NotEqual(CallbackAttemptStatus.Succeeded, pendingAttempt.Status);
            Assert.True(pendingAttempt.RetryCount is 0 or 1);
            Assert.True(pendingAttempt.NextRetryAtUtc is not null || pendingAttempt.Status == CallbackAttemptStatus.InProgress);
            Assert.Null(pendingAttempt.AcknowledgedAtUtc);
            Assert.True(await dbContext.LabEventLogs.AnyAsync(x => x.EventType == "PushDeliveryFailed" && x.CorrelationId == "corr-failure-flow"));
        });

        using HttpResponseMessage pullResponse = await client.GetAsync("/fcc/VL-MW-BT001/transactions?limit=10");
        string pullBody = await pullResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, pullResponse.StatusCode);
        Assert.Contains("corr-failure-flow", pullBody, StringComparison.Ordinal);

        string externalTransactionId = await factory.GetTransactionExternalIdAsync("corr-failure-flow");

        using HttpResponseMessage pushResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/transactions/push",
            $$"""{"transactionIds":["{{externalTransactionId}}"]}""");
        string pushBody = await pushResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, pushResponse.StatusCode);
        Assert.Contains("Succeeded", pushBody, StringComparison.Ordinal);

        await factory.WithDbContextAsync(async dbContext =>
        {
            SimulatedTransaction transaction = await dbContext.SimulatedTransactions.SingleAsync(x => x.CorrelationId == "corr-failure-flow");
            int transactionCount = await dbContext.SimulatedTransactions.CountAsync(x => x.CorrelationId == "corr-failure-flow");
            List<CallbackAttempt> attempts = await dbContext.CallbackAttempts
                .Where(x => x.SimulatedTransactionId == transaction.Id)
                .OrderBy(x => x.AttemptNumber)
                .ToListAsync();
            int successLogs = await dbContext.LabEventLogs.CountAsync(x => x.CorrelationId == "corr-failure-flow" && x.EventType == "TransactionPushed");
            int retryLogs = await dbContext.LabEventLogs.CountAsync(x => x.CorrelationId == "corr-failure-flow" && x.EventType == "CallbackRetryScheduled");

            Assert.Equal(1, transactionCount);
            Assert.Equal(SimulatedTransactionStatus.Delivered, transaction.Status);
            Assert.Equal(1, attempts.Count(x => x.Status == CallbackAttemptStatus.Succeeded));
            Assert.Equal(1, attempts.Count(x => x.AcknowledgedAtUtc.HasValue));
            Assert.Contains(attempts, x => x.RetryCount == 1 || x.Status == CallbackAttemptStatus.Succeeded);
            Assert.Equal(1, successLogs);
            Assert.Equal(1, retryLogs);
        });
    }

    private static async Task RunNormalFlowAsync(
        HttpClient client,
        ForecourtFixture fixture,
        string correlationId,
        string dispenseStartJson,
        string dispenseStopJson)
    {
        using HttpResponseMessage liftResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/lift",
            $$"""{"correlationId":"{{correlationId}}"}""");
        using HttpResponseMessage startResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/dispense",
            dispenseStartJson);
        using HttpResponseMessage stopResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/dispense",
            dispenseStopJson);
        using HttpResponseMessage hangResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/hang",
            $$"""{"correlationId":"{{correlationId}}"}""");

        Assert.Equal(HttpStatusCode.OK, liftResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, hangResponse.StatusCode);
    }

    private static async Task<ForecourtFixture> ConfigureSiteAsync(VirtualLabApiFactory factory, string profileKey)
    {
        ForecourtFixture? fixture = null;

        await factory.WithDbContextAsync(async dbContext =>
        {
            Site site = await dbContext.Sites.SingleAsync(x => x.SiteCode == "VL-MW-BT001");
            FccSimulatorProfile profile = await dbContext.FccSimulatorProfiles.SingleAsync(x => x.ProfileKey == profileKey);
            Pump pump = await dbContext.Pumps.SingleAsync(x => x.SiteId == site.Id && x.PumpNumber == 1);
            Nozzle nozzle = await dbContext.Nozzles.SingleAsync(x => x.PumpId == pump.Id && x.NozzleNumber == 1);

            dbContext.CallbackAttempts.RemoveRange(dbContext.CallbackAttempts);
            dbContext.LabEventLogs.RemoveRange(dbContext.LabEventLogs);
            dbContext.SimulatedTransactions.RemoveRange(dbContext.SimulatedTransactions);
            dbContext.PreAuthSessions.RemoveRange(dbContext.PreAuthSessions);

            site.ActiveFccSimulatorProfileId = profile.Id;
            site.DeliveryMode = profile.DeliveryMode;
            site.PreAuthMode = profile.PreAuthMode;
            site.InboundAuthMode = SimulatedAuthMode.None;
            site.ApiKeyHeaderName = string.Empty;
            site.ApiKeyValue = string.Empty;
            site.BasicAuthUsername = string.Empty;
            site.BasicAuthPassword = string.Empty;
            site.UpdatedAtUtc = DateTimeOffset.UtcNow;

            nozzle.State = NozzleState.Idle;
            nozzle.SimulationStateJson = "{}";
            nozzle.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await dbContext.SaveChangesAsync();
            fixture = new ForecourtFixture(site.Id, pump.Id, nozzle.Id);
        });

        return fixture ?? throw new InvalidOperationException("Unable to configure site.");
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json)
    {
        return client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static string ReadNozzleState(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("nozzle").GetProperty("state").GetString()
            ?? throw new InvalidOperationException("Missing nozzle state.");
    }

    private static bool ReadTransactionGenerated(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("transactionGenerated").GetBoolean();
    }

    private static string ReadRequiredString(string json, string propertyName)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException($"Missing '{propertyName}'.");
    }

    private static int ReadRequiredInt(string json, string propertyName)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).GetInt32();
    }

    private sealed record ForecourtFixture(Guid SiteId, Guid PumpId, Guid NozzleId);
}

internal static class ForecourtSimulationTestExtensions
{
    public static async Task<string> GetTransactionExternalIdAsync(this VirtualLabApiFactory factory, string correlationId)
    {
        string? externalId = null;

        await factory.WithDbContextAsync(async dbContext =>
        {
            externalId = await dbContext.SimulatedTransactions
                .Where(x => x.CorrelationId == correlationId)
                .Select(x => x.ExternalTransactionId)
                .SingleAsync();
        });

        return externalId ?? throw new InvalidOperationException("Missing external transaction id.");
    }
}

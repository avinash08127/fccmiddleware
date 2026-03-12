using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;

namespace VirtualLab.Tests.Api;

public sealed class PreAuthSimulationTests
{
    [Fact]
    public async Task CreateOnlyProfileAutoAuthorizesAndExposesTimeline()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        await ConfigureSiteAsync(factory, "generic-create-only");

        using HttpResponseMessage createResponse = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/preauth/create",
            """{"amount":15000,"pump":1,"nozzle":1,"correlationId":"corr-create-only"}""");

        string createBody = await createResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.Equal("authorized", ReadRequiredString(createBody, "status"));

        using HttpResponseMessage sessionsResponse = await client.GetAsync("/api/preauth-sessions?correlationId=corr-create-only");
        string sessionsBody = await sessionsResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, sessionsResponse.StatusCode);
        using JsonDocument sessionsDocument = JsonDocument.Parse(sessionsBody);
        JsonElement listedSession = sessionsDocument.RootElement.EnumerateArray().Single();
        Assert.Equal("AUTHORIZED", listedSession.GetProperty("status").GetString());
        Assert.Contains("StateTransition", listedSession.GetProperty("timelineJson").GetString(), StringComparison.Ordinal);

        await factory.WithDbContextAsync(async dbContext =>
        {
            PreAuthSession session = await dbContext.PreAuthSessions.SingleAsync(x => x.CorrelationId == "corr-create-only");

            Assert.Equal(PreAuthFlowMode.CreateOnly, session.Mode);
            Assert.Equal(PreAuthSessionStatus.Authorized, session.Status);
            Assert.NotNull(session.AuthorizedAtUtc);
            Assert.NotNull(session.ExpiresAtUtc);
            Assert.Contains("AUTHORIZED", session.TimelineJson, StringComparison.Ordinal);

            List<LabEventLog> logs = await dbContext.LabEventLogs
                .Where(x => x.CorrelationId == "corr-create-only")
                .ToListAsync();

            Assert.Contains(logs, x => x.Category == "FccRequest");
            Assert.Contains(logs, x => x.Category == "FccResponse");
            Assert.Contains(logs, x => x.Category == "StateTransition");
        });
    }

    [Fact]
    public async Task CreateThenAuthorizeRejectsOutOfOrderFlow()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        await ConfigureSiteAsync(factory, "doms-like");

        using HttpResponseMessage missingAuthorize = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/preauth/authorize",
            """{"preauthId":"PA-MISSING","amount":15000,"correlationId":"corr-sequence"}""");

        Assert.Equal(HttpStatusCode.NotFound, missingAuthorize.StatusCode);

        using HttpResponseMessage createResponse = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/preauth/create",
            """{"amount":15000,"pump":1,"nozzle":1,"correlationId":"corr-sequence"}""");

        string createBody = await createResponse.Content.ReadAsStringAsync();
        string preAuthId = ReadRequiredString(createBody, "preauthId");

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.Equal("pending", ReadRequiredString(createBody, "status"));

        using HttpResponseMessage authorizeResponse = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/preauth/authorize",
            $$"""{"preauthId":"{{preAuthId}}","amount":15000,"correlationId":"corr-sequence"}""");

        string authorizeBody = await authorizeResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, authorizeResponse.StatusCode);
        Assert.Equal("authorized", ReadRequiredString(authorizeBody, "status"));

        using HttpResponseMessage duplicateAuthorize = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/preauth/authorize",
            $$"""{"preauthId":"{{preAuthId}}","amount":15000,"correlationId":"corr-sequence"}""");

        Assert.Equal(HttpStatusCode.Conflict, duplicateAuthorize.StatusCode);

        await factory.WithDbContextAsync(async dbContext =>
        {
            PreAuthSession session = await dbContext.PreAuthSessions.SingleAsync(x => x.CorrelationId == "corr-sequence");
            Assert.Equal(PreAuthSessionStatus.Authorized, session.Status);

            int rejectedCount = await dbContext.LabEventLogs
                .CountAsync(x => x.CorrelationId == "corr-sequence" && x.EventType == "PreAuthSequenceRejected");

            Assert.True(rejectedCount >= 2);
        });
    }

    [Fact]
    public async Task CancelAndExpirePathsArePersistedAndLogged()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        await ConfigureSiteAsync(factory, "doms-like");

        using HttpResponseMessage createResponse = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/preauth/create",
            """{"amount":15000,"pump":1,"nozzle":1,"correlationId":"corr-cancel"}""");
        string createBody = await createResponse.Content.ReadAsStringAsync();
        string cancelPreAuthId = ReadRequiredString(createBody, "preauthId");

        using HttpResponseMessage cancelResponse = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/preauth/cancel",
            $$"""{"preauthId":"{{cancelPreAuthId}}","correlationId":"corr-cancel"}""");

        string cancelBody = await cancelResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
        Assert.Equal("cancelled", ReadRequiredString(cancelBody, "status"));

        using HttpResponseMessage expiringCreateResponse = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/preauth/create",
            """{"amount":9000,"pump":1,"nozzle":1,"correlationId":"corr-expire","expiresInSeconds":0}""");
        string expiringCreateBody = await expiringCreateResponse.Content.ReadAsStringAsync();
        string expiringPreAuthId = ReadRequiredString(expiringCreateBody, "preauthId");

        using HttpResponseMessage expiredAuthorizeResponse = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/preauth/authorize",
            $$"""{"preauthId":"{{expiringPreAuthId}}","amount":9000,"correlationId":"corr-expire"}""");

        string expiredAuthorizeBody = await expiredAuthorizeResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, expiredAuthorizeResponse.StatusCode);
        Assert.Equal("EXPIRED", ReadRequiredString(expiredAuthorizeBody, "status"));

        await factory.WithDbContextAsync(async dbContext =>
        {
            PreAuthSession cancelled = await dbContext.PreAuthSessions.SingleAsync(x => x.CorrelationId == "corr-cancel");
            PreAuthSession expired = await dbContext.PreAuthSessions.SingleAsync(x => x.CorrelationId == "corr-expire");

            Assert.Equal(PreAuthSessionStatus.Cancelled, cancelled.Status);
            Assert.Equal(PreAuthSessionStatus.Expired, expired.Status);

            Assert.Contains("CANCELLED", cancelled.TimelineJson, StringComparison.Ordinal);
            Assert.Contains("EXPIRED", expired.TimelineJson, StringComparison.Ordinal);

            Assert.True(await dbContext.LabEventLogs.AnyAsync(x => x.CorrelationId == "corr-expire" && x.EventType == "PreAuthExpired"));
        });
    }

    [Fact]
    public async Task FailureInjectionMarksSessionFailedAndLogsHook()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        await ConfigureSiteAsync(factory, "doms-like");

        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/fcc/VL-MW-BT001/preauth/create",
            """{"amount":15000,"pump":1,"nozzle":1,"correlationId":"corr-failure","simulateFailure":true,"failureMessage":"forced failure"}""");

        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("failed", ReadRequiredString(body, "status"));
        Assert.Contains("forced failure", body, StringComparison.Ordinal);

        await factory.WithDbContextAsync(async dbContext =>
        {
            PreAuthSession session = await dbContext.PreAuthSessions.SingleAsync(x => x.CorrelationId == "corr-failure");
            Assert.Equal(PreAuthSessionStatus.Failed, session.Status);
            Assert.Contains("FAILED", session.TimelineJson, StringComparison.Ordinal);

            Assert.True(await dbContext.LabEventLogs.AnyAsync(x =>
                x.CorrelationId == "corr-failure" &&
                x.EventType == "PreAuthFailureInjected" &&
                x.Category == "PreAuthSequence"));
        });
    }

    private static async Task ConfigureSiteAsync(VirtualLabApiFactory factory, string profileKey)
    {
        await factory.WithDbContextAsync(async dbContext =>
        {
            Site site = await dbContext.Sites.SingleAsync(x => x.SiteCode == "VL-MW-BT001");
            FccSimulatorProfile profile = await dbContext.FccSimulatorProfiles.SingleAsync(x => x.ProfileKey == profileKey);

            site.ActiveFccSimulatorProfileId = profile.Id;
            site.PreAuthMode = profile.PreAuthMode;
            site.InboundAuthMode = SimulatedAuthMode.None;
            site.ApiKeyHeaderName = string.Empty;
            site.ApiKeyValue = string.Empty;
            site.BasicAuthUsername = string.Empty;
            site.BasicAuthPassword = string.Empty;
            site.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await dbContext.SaveChangesAsync();
        });
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json)
    {
        return client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static string ReadRequiredString(string json, string propertyName)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException($"Missing '{propertyName}'.");
    }
}

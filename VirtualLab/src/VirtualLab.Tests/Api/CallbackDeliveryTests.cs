using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;

namespace VirtualLab.Tests.Api;

public sealed class CallbackDeliveryTests
{
    [Fact]
    public async Task FccHealthAndPumpStatusEndpointsReturnSiteAwareState()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        ForecourtFixture fixture = await ConfigureSiteAsync(factory, callbackUrl: null);

        using HttpResponseMessage liftResponse = await PostJsonAsync(
            client,
            $"/api/sites/{fixture.SiteId}/pumps/{fixture.PumpId}/nozzles/{fixture.NozzleId}/lift",
            """{"correlationId":"corr-status"}""");

        Assert.Equal(HttpStatusCode.OK, liftResponse.StatusCode);

        using HttpRequestMessage healthRequest = new(HttpMethod.Get, "/fcc/VL-MW-BT001/health");
        healthRequest.Headers.Add("X-Api-Key", "demo-site-key");
        using HttpResponseMessage healthResponse = await client.SendAsync(healthRequest);
        string healthBody = await healthResponse.Content.ReadAsStringAsync();

        using HttpRequestMessage statusRequest = new(HttpMethod.Get, "/fcc/VL-MW-BT001/pump-status");
        statusRequest.Headers.Add("X-Api-Key", "demo-site-key");
        using HttpResponseMessage pumpStatusResponse = await client.SendAsync(statusRequest);
        string pumpStatusBody = await pumpStatusResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pumpStatusResponse.StatusCode);

        using (JsonDocument healthDocument = JsonDocument.Parse(healthBody))
        {
            Assert.Equal("VL-MW-BT001", healthDocument.RootElement.GetProperty("siteCode").GetString());
            Assert.Equal("HYBRID", healthDocument.RootElement.GetProperty("deliveryMode").GetString());
            Assert.Equal("ok", healthDocument.RootElement.GetProperty("status").GetString());
        }

        using (JsonDocument pumpStatusDocument = JsonDocument.Parse(pumpStatusBody))
        {
            JsonElement firstPump = pumpStatusDocument.RootElement.GetProperty("pumps").EnumerateArray().First();
            JsonElement firstNozzle = firstPump.GetProperty("nozzles").EnumerateArray().First();

            Assert.Equal("VL-MW-BT001", pumpStatusDocument.RootElement.GetProperty("siteCode").GetString());
            Assert.Equal("LIFTED", firstPump.GetProperty("state").GetString());
            Assert.Equal("LIFTED", firstNozzle.GetProperty("state").GetString());
        }
    }

    private static async Task<ForecourtFixture> ConfigureSiteAsync(VirtualLabApiFactory factory, Uri? callbackUrl)
    {
        ForecourtFixture? fixture = null;

        await factory.WithDbContextAsync(async dbContext =>
        {
            Site site = await dbContext.Sites.SingleAsync(x => x.SiteCode == "VL-MW-BT001");
            FccSimulatorProfile profile = await dbContext.FccSimulatorProfiles.SingleAsync(x => x.ProfileKey == "doms-like");
            Pump pump = await dbContext.Pumps.SingleAsync(x => x.SiteId == site.Id && x.PumpNumber == 1);
            Nozzle nozzle = await dbContext.Nozzles.SingleAsync(x => x.PumpId == pump.Id && x.NozzleNumber == 1);
            CallbackTarget callbackTarget = await dbContext.CallbackTargets.SingleAsync(x => x.SiteId == site.Id && x.TargetKey == "demo-callback");

            dbContext.CallbackAttempts.RemoveRange(dbContext.CallbackAttempts);
            dbContext.LabEventLogs.RemoveRange(dbContext.LabEventLogs);
            dbContext.SimulatedTransactions.RemoveRange(dbContext.SimulatedTransactions);
            dbContext.PreAuthSessions.RemoveRange(dbContext.PreAuthSessions);

            site.ActiveFccSimulatorProfileId = profile.Id;
            site.DeliveryMode = profile.DeliveryMode;
            site.PreAuthMode = profile.PreAuthMode;
            site.InboundAuthMode = SimulatedAuthMode.ApiKey;
            site.ApiKeyHeaderName = "X-Api-Key";
            site.ApiKeyValue = "demo-site-key";
            site.BasicAuthUsername = string.Empty;
            site.BasicAuthPassword = string.Empty;
            site.UpdatedAtUtc = DateTimeOffset.UtcNow;

            callbackTarget.CallbackUrl = callbackUrl ?? callbackTarget.CallbackUrl;
            callbackTarget.AuthMode = SimulatedAuthMode.None;
            callbackTarget.ApiKeyHeaderName = string.Empty;
            callbackTarget.ApiKeyValue = string.Empty;
            callbackTarget.BasicAuthUsername = string.Empty;
            callbackTarget.BasicAuthPassword = string.Empty;

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

    private sealed record ForecourtFixture(Guid SiteId, Guid PumpId, Guid NozzleId);
}

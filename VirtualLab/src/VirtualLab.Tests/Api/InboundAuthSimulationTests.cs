using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Tests.Api;

public sealed class InboundAuthSimulationTests
{
    [Fact]
    public async Task ManagementApiRemainsOpenWithoutAuth()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/sites");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FccEndpointUsesSiteSpecificApiKeyOverrideAndLogsFailures()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage rejectedRequest = new(HttpMethod.Get, "/fcc/VL-MW-BT001/health");
        rejectedRequest.Headers.Add("X-Api-Key", "demo-profile-key");

        using HttpResponseMessage rejectedWithProfileKey = await client.SendAsync(rejectedRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, rejectedWithProfileKey.StatusCode);

        await factory.AssertLatestAuthFailureAsync(
            eventType: "FccAuthRejected",
            forbiddenFragments: ["demo-site-key", "demo-profile-key", "demo-password"]);

        using HttpRequestMessage authorizedRequest = new(HttpMethod.Get, "/fcc/VL-MW-BT001/health");
        authorizedRequest.Headers.Add("X-Api-Key", "demo-site-key");

        using HttpResponseMessage authorizedResponse = await client.SendAsync(authorizedRequest);
        string responseBody = await authorizedResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, authorizedResponse.StatusCode);
        using (JsonDocument document = JsonDocument.Parse(responseBody))
        {
            Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task FccEndpointFallsBackToProfileBasicAuthWhenSiteOverrideIsCleared()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        await factory.WithDbContextAsync(async dbContext =>
        {
            Site site = await dbContext.Sites.SingleAsync(x => x.SiteCode == "VL-MW-BT001");
            FccSimulatorProfile basicAuthProfile = await dbContext.FccSimulatorProfiles
                .SingleAsync(x => x.ProfileKey == "generic-create-then-authorize");

            site.ActiveFccSimulatorProfileId = basicAuthProfile.Id;
            site.InboundAuthMode = SimulatedAuthMode.None;
            site.ApiKeyHeaderName = string.Empty;
            site.ApiKeyValue = string.Empty;
            site.BasicAuthUsername = string.Empty;
            site.BasicAuthPassword = string.Empty;
            site.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await dbContext.SaveChangesAsync();
        });

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("demo:demo-password")));

        using StringContent content = new("""{"amount":15000,"pump":1,"nozzle":1}""", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/fcc/VL-MW-BT001/preauth/create", content);
        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (JsonDocument document = JsonDocument.Parse(responseBody))
        {
            Assert.Equal("pending", document.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task CallbackEndpointEnforcesConfiguredBasicAuth()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using StringContent body = new("""{"correlationId":"corr-default-flow","transactionId":"TX-0001"}""", Encoding.UTF8, "application/json");
        using HttpResponseMessage unauthorizedResponse = await client.PostAsync("/callbacks/demo-callback", body);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);

        await factory.AssertLatestAuthFailureAsync(
            eventType: "CallbackAuthRejected",
            forbiddenFragments: ["demo-password"]);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("demo:demo-password")));

        using StringContent authorizedBody = new("""{"correlationId":"corr-default-flow","transactionId":"TX-0001"}""", Encoding.UTF8, "application/json");
        using HttpResponseMessage authorizedResponse = await client.PostAsync("/callbacks/demo-callback", authorizedBody);
        string responseBody = await authorizedResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Accepted, authorizedResponse.StatusCode);
        Assert.Contains("\"accepted\":true", responseBody);
    }

    [Fact]
    public async Task OptionsRequestReturnsCorsHeadersForConfiguredUiOrigin()
    {
        using VirtualLabApiFactory factory = new(new Dictionary<string, string?>
        {
            ["VirtualLab:Cors:AllowedOrigins:0"] = "https://virtual-lab-ui.example.com",
        });
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Options, "/fcc/VL-MW-BT001/health");

        request.Headers.Add("Origin", "https://virtual-lab-ui.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("https://virtual-lab-ui.example.com", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}

internal sealed class VirtualLabApiFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> configurationOverrides;
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"virtual-lab-tests-{Guid.NewGuid():N}.db");

    public VirtualLabApiFactory(IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        this.configurationOverrides = configurationOverrides ?? new Dictionary<string, string?>();
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            Dictionary<string, string?> settings = new()
            {
                ["VirtualLab:Persistence:ConnectionString"] = $"Data Source={databasePath}",
                ["VirtualLab:Seed:ApplyOnStartup"] = "true",
                ["VirtualLab:Seed:ResetOnStartup"] = "true",
                ["VirtualLab:Callbacks:WorkerPollIntervalMs"] = "100",
                ["VirtualLab:Callbacks:MaxRetryCount"] = "2",
                ["VirtualLab:Callbacks:RetryDelaysSeconds:0"] = "30",
                ["VirtualLab:Callbacks:RetryDelaysSeconds:1"] = "30",
                ["VirtualLab:Callbacks:RequestTimeoutSeconds"] = "2",
            };

            foreach ((string key, string? value) in configurationOverrides)
            {
                settings[key] = value;
            }

            configurationBuilder.AddInMemoryCollection(settings);
        });
    }

    public async Task WithDbContextAsync(Func<VirtualLabDbContext, Task> action)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        VirtualLabDbContext dbContext = scope.ServiceProvider.GetRequiredService<VirtualLabDbContext>();
        await action(dbContext);
    }

    public async Task<TResult> WithDbContextAsync<TResult>(Func<VirtualLabDbContext, Task<TResult>> action)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        VirtualLabDbContext dbContext = scope.ServiceProvider.GetRequiredService<VirtualLabDbContext>();
        return await action(dbContext);
    }

    public async Task AssertLatestAuthFailureAsync(string eventType, IReadOnlyList<string> forbiddenFragments)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        VirtualLabDbContext dbContext = scope.ServiceProvider.GetRequiredService<VirtualLabDbContext>();

        LabEventLog logEntry = await dbContext.LabEventLogs
            .AsNoTracking()
            .Where(x => x.Category == "AuthFailure")
            .OrderByDescending(x => x.OccurredAtUtc)
            .FirstAsync();

        Assert.Equal("Warning", logEntry.Severity);
        Assert.Equal("AuthFailure", logEntry.Category);
        Assert.Equal(eventType, logEntry.EventType);
        Assert.NotEmpty(logEntry.MetadataJson);

        JsonElement metadata = JsonSerializer.Deserialize<JsonElement>(logEntry.MetadataJson);
        string? method = metadata.GetProperty("method").GetString();
        Assert.True(method is "GET" or "POST");
        Assert.NotNull(metadata.GetProperty("path").GetString());

        foreach (string forbiddenFragment in forbiddenFragments)
        {
            Assert.DoesNotContain(forbiddenFragment, logEntry.MetadataJson, StringComparison.Ordinal);
            Assert.DoesNotContain(forbiddenFragment, logEntry.Message, StringComparison.Ordinal);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        try
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
        catch (IOException)
        {
        }
    }
}

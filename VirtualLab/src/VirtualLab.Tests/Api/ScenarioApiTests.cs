using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VirtualLab.Tests.Api;

public sealed class ScenarioApiTests
{
    [Fact]
    public async Task ScenarioLibraryAndRunnerSupportDeterministicExecutionAndExport()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage listResponse = await client.GetAsync("/api/scenarios");
        string listBody = await listResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        using JsonDocument listDocument = JsonDocument.Parse(listBody);
        JsonElement[] definitions = listDocument.RootElement.GetProperty("definitions").EnumerateArray().ToArray();
        Assert.Contains(definitions, definition => definition.GetProperty("scenarioKey").GetString() == "fiscalized-preauth-success");
        Assert.Contains(definitions, definition => definition.GetProperty("scenarioKey").GetString() == "offline-pull-catch-up");

        using HttpResponseMessage firstRunResponse = await client.PostAsJsonAsync(
            "/api/scenarios/run",
            new
            {
                scenarioKey = "offline-pull-catch-up",
                replaySeed = 424242,
            });
        string firstRunBody = await firstRunResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, firstRunResponse.StatusCode);

        using JsonDocument firstRunDocument = JsonDocument.Parse(firstRunBody);
        string firstReplaySignature = firstRunDocument.RootElement.GetProperty("replaySignature").GetString()!;
        Guid firstRunId = firstRunDocument.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Completed", firstRunDocument.RootElement.GetProperty("status").GetString());
        Assert.True(firstRunDocument.RootElement.GetProperty("steps").GetArrayLength() >= 5);
        Assert.True(firstRunDocument.RootElement.GetProperty("assertions").GetArrayLength() >= 2);

        using HttpResponseMessage secondRunResponse = await client.PostAsJsonAsync(
            "/api/scenarios/run",
            new
            {
                scenarioKey = "offline-pull-catch-up",
                replaySeed = 424242,
            });
        string secondRunBody = await secondRunResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, secondRunResponse.StatusCode);

        using JsonDocument secondRunDocument = JsonDocument.Parse(secondRunBody);
        Assert.Equal(firstReplaySignature, secondRunDocument.RootElement.GetProperty("replaySignature").GetString());
        Assert.Equal("Completed", secondRunDocument.RootElement.GetProperty("status").GetString());

        using HttpResponseMessage runDetailResponse = await client.GetAsync($"/api/scenarios/runs/{firstRunId}");
        string runDetailBody = await runDetailResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, runDetailResponse.StatusCode);
        Assert.Contains("Acknowledged", runDetailBody, StringComparison.Ordinal);
        Assert.Contains("contractValidation", runDetailBody, StringComparison.Ordinal);

        using HttpResponseMessage exportResponse = await client.GetAsync("/api/scenarios/export");
        string exportBody = await exportResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);

        using JsonDocument exportDocument = JsonDocument.Parse(exportBody);
        Assert.True(exportDocument.RootElement.GetArrayLength() >= 4);

        using HttpResponseMessage importResponse = await client.PostAsJsonAsync(
            "/api/scenarios/import",
            new
            {
                replaceExisting = true,
                definitions = JsonSerializer.Deserialize<object>(exportBody),
            });
        string importBody = await importResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
        Assert.Contains("\"updatedCount\":4", importBody, StringComparison.Ordinal);
    }
}

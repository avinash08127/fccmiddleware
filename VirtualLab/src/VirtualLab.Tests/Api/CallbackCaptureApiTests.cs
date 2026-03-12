using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VirtualLab.Tests.Api;

public sealed class CallbackCaptureApiTests
{
    [Fact]
    public async Task CallbackHistoryIncludesRejectedCapturedAndReplayedEntries()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        const string targetKey = "demo-callback";
        const string payload = """{"correlationId":"corr-callback-history","transactionId":"TX-0001","amount":9725.50}""";

        using HttpResponseMessage rejectedResponse = await client.PostAsync(
            $"/callbacks/{targetKey}",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, rejectedResponse.StatusCode);

        using HttpRequestMessage captureRequest = new(HttpMethod.Post, $"/callbacks/{targetKey}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        captureRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("demo:demo-password")));

        using HttpResponseMessage captureResponse = await client.SendAsync(captureRequest);
        string captureBody = await captureResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, captureResponse.StatusCode);
        Assert.Contains("accepted", captureBody, StringComparison.OrdinalIgnoreCase);

        using HttpResponseMessage historyResponse = await client.GetAsync($"/api/callbacks/{targetKey}/history?limit=10");
        string historyBody = await historyResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);

        using JsonDocument historyDocument = JsonDocument.Parse(historyBody);
        JsonElement[] historyItems = historyDocument.RootElement.EnumerateArray().ToArray();
        Assert.Contains(historyItems, item => item.GetProperty("authOutcome").GetString() == "Rejected");

        JsonElement originalCapture = historyItems.First(item =>
            item.GetProperty("authOutcome").GetString() != "Rejected" &&
            !item.GetProperty("isReplay").GetBoolean());
        Guid captureId = originalCapture.GetProperty("id").GetGuid();
        string correlationId = originalCapture.GetProperty("correlationId").GetString()!;

        using HttpResponseMessage replayResponse = await client.PostAsync(
            $"/api/callbacks/{targetKey}/history/{captureId}/replay",
            null);
        string replayBody = await replayResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.Contains("Callback replay captured successfully.", replayBody, StringComparison.Ordinal);

        using HttpResponseMessage replayHistoryResponse = await client.GetAsync($"/api/callbacks/{targetKey}/history?limit=10");
        string replayHistoryBody = await replayHistoryResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, replayHistoryResponse.StatusCode);

        using JsonDocument replayHistoryDocument = JsonDocument.Parse(replayHistoryBody);
        JsonElement replayCapture = replayHistoryDocument.RootElement
            .EnumerateArray()
            .First(item => item.GetProperty("isReplay").GetBoolean());

        Assert.Equal(captureId, replayCapture.GetProperty("replayedFromId").GetGuid());
        Assert.Equal(correlationId, replayCapture.GetProperty("correlationId").GetString());
        Assert.Equal(202, replayCapture.GetProperty("responseStatusCode").GetInt32());
    }
}

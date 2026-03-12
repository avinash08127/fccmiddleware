using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
namespace VirtualLab.Tests.Simulators;

/// <summary>
/// TEST-5.3: Petronite REST/JSON simulator E2E scenarios.
/// Exercises management API endpoints to verify simulator lifecycle,
/// nozzle discovery, order injection, webhook registration, nozzle state
/// control, reset behavior, and multi-pump operations.
/// </summary>
[Collection("Simulators")]
public sealed class PetroniteSimulatorE2ETests
{
    private readonly SimulatorTestFixture _fixture;

    public PetroniteSimulatorE2ETests(SimulatorTestFixture fixture)
    {
        _fixture = fixture;
    }

    // ---------------------------------------------------------------------
    // 1. OAuth & Initial State
    // ---------------------------------------------------------------------

    [Fact]
    public async Task OAuthAndInitialState_SimulatorStartsWithNozzleAssignmentsAndNoActiveTokens()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        using HttpResponseMessage response = await client.GetAsync("/api/petronite/state");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        // Nozzle assignments are populated on startup
        JsonElement nozzleAssignments = root.GetProperty("nozzleAssignments");
        Assert.True(nozzleAssignments.GetArrayLength() > 0, "Expected nozzle assignments to be populated on startup.");

        // No active tokens initially (no OAuth requests have been made)
        int activeTokenCount = root.GetProperty("activeTokenCount").GetInt32();
        Assert.Equal(0, activeTokenCount);

        // No orders initially
        int orderCount = root.GetProperty("orderCount").GetInt32();
        Assert.Equal(0, orderCount);
    }

    // ---------------------------------------------------------------------
    // 2. Nozzle Discovery
    // ---------------------------------------------------------------------

    [Fact]
    public async Task NozzleDiscovery_AllPumpNozzleAssignmentsPresent_WithDefaultProductCodes()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        using HttpResponseMessage response = await client.GetAsync("/api/petronite/state");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement nozzleAssignments = document.RootElement.GetProperty("nozzleAssignments");
        int assignmentCount = nozzleAssignments.GetArrayLength();

        // Default pump count is 4, so we expect 4 nozzle assignments
        Assert.True(assignmentCount >= 4, $"Expected at least 4 nozzle assignments but found {assignmentCount}.");

        // Verify each assignment has expected properties
        HashSet<int> pumpNumbers = new();
        foreach (JsonElement assignment in nozzleAssignments.EnumerateArray())
        {
            int pumpNumber = assignment.GetProperty("pumpNumber").GetInt32();
            pumpNumbers.Add(pumpNumber);

            string? productCode = assignment.GetProperty("productCode").GetString();
            Assert.False(string.IsNullOrWhiteSpace(productCode), $"Pump {pumpNumber} should have a product code.");

            // Pumps 1-2 default to UNL95, pumps 3+ default to DSL
            if (pumpNumber <= 2)
            {
                Assert.Equal("UNL95", productCode);
            }
            else
            {
                Assert.Equal("DSL", productCode);
            }

            Assert.False(assignment.GetProperty("isNozzleLifted").GetBoolean(),
                $"Pump {pumpNumber} nozzle should not be lifted initially.");
        }

        // Verify pumps 1 through 4 are all present
        for (int i = 1; i <= 4; i++)
        {
            Assert.True(pumpNumbers.Contains(i), $"Expected pump {i} in nozzle assignments.");
        }
    }

    // ---------------------------------------------------------------------
    // 3. Order Lifecycle
    // ---------------------------------------------------------------------

    [Fact]
    public async Task OrderLifecycle_SetNozzleLiftedThenInjectTransaction_OrderCompletedWithCount1()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Lift nozzle on pump 1
        using HttpResponseMessage liftResponse = await client.PostAsJsonAsync(
            "/api/petronite/set-nozzle-state",
            new { pumpNumber = 1, isNozzleLifted = true });
        Assert.Equal(HttpStatusCode.OK, liftResponse.StatusCode);

        // Inject a transaction on pump 1
        using HttpResponseMessage injectResponse = await client.PostAsJsonAsync(
            "/api/petronite/inject-transaction",
            new { pumpNumber = 1, nozzleNumber = 1, amount = 25.00m });
        string injectBody = await injectResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, injectResponse.StatusCode);
        using JsonDocument injectDoc = JsonDocument.Parse(injectBody);
        JsonElement injectRoot = injectDoc.RootElement;
        Assert.Equal("completed", injectRoot.GetProperty("status").GetString());

        // Verify state shows 1 order
        using HttpResponseMessage stateResponse = await client.GetAsync("/api/petronite/state");
        string stateBody = await stateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);
        using JsonDocument stateDoc = JsonDocument.Parse(stateBody);
        Assert.Equal(1, stateDoc.RootElement.GetProperty("orderCount").GetInt32());

        // Verify the order in the orders array is completed
        JsonElement orders = stateDoc.RootElement.GetProperty("orders");
        Assert.Equal(1, orders.GetArrayLength());

        JsonElement order = orders[0];
        Assert.Equal("Completed", order.GetProperty("status").GetString());
        Assert.Equal(1, order.GetProperty("pumpNumber").GetInt32());
    }

    // ---------------------------------------------------------------------
    // 4. Webhook Registration
    // ---------------------------------------------------------------------

    [Fact]
    public async Task WebhookRegistration_SetUrl_VerifyInState_InjectTransaction_OrderCompleted()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        string webhookUrl = "https://example.com/webhook/petronite";

        // Register webhook URL
        using HttpResponseMessage setUrlResponse = await client.PostAsJsonAsync(
            "/api/petronite/set-webhook-url",
            new { url = webhookUrl });
        string setUrlBody = await setUrlResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, setUrlResponse.StatusCode);
        using (JsonDocument setUrlDoc = JsonDocument.Parse(setUrlBody))
        {
            Assert.Equal(webhookUrl, setUrlDoc.RootElement.GetProperty("webhookUrl").GetString());
        }

        // Verify URL is reflected in state
        using HttpResponseMessage stateResponse1 = await client.GetAsync("/api/petronite/state");
        string stateBody1 = await stateResponse1.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse1.StatusCode);
        using (JsonDocument stateDoc1 = JsonDocument.Parse(stateBody1))
        {
            Assert.Equal(webhookUrl, stateDoc1.RootElement.GetProperty("webhookCallbackUrl").GetString());
        }

        // Lift nozzle and inject transaction
        using HttpResponseMessage liftResponse = await client.PostAsJsonAsync(
            "/api/petronite/set-nozzle-state",
            new { pumpNumber = 1, isNozzleLifted = true });
        Assert.Equal(HttpStatusCode.OK, liftResponse.StatusCode);

        using HttpResponseMessage injectResponse = await client.PostAsJsonAsync(
            "/api/petronite/inject-transaction",
            new { pumpNumber = 1, nozzleNumber = 1, amount = 42.00m });
        string injectBody = await injectResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, injectResponse.StatusCode);
        using JsonDocument injectDoc = JsonDocument.Parse(injectBody);
        JsonElement injectRoot = injectDoc.RootElement;

        // Order should be completed (webhook would be sent to the registered URL)
        Assert.Equal("completed", injectRoot.GetProperty("status").GetString());
        Assert.True(injectRoot.GetProperty("webhookSent").GetBoolean(),
            "Webhook should indicate it was sent since a webhook URL is registered.");
    }

    // ---------------------------------------------------------------------
    // 5. Nozzle State Control
    // ---------------------------------------------------------------------

    [Fact]
    public async Task NozzleStateControl_LiftNozzle_VerifyState_SetDown_VerifyStateChanged()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Lift nozzle on pump 1
        using HttpResponseMessage liftResponse = await client.PostAsJsonAsync(
            "/api/petronite/set-nozzle-state",
            new { pumpNumber = 1, isNozzleLifted = true });
        string liftBody = await liftResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, liftResponse.StatusCode);
        using (JsonDocument liftDoc = JsonDocument.Parse(liftBody))
        {
            Assert.True(liftDoc.RootElement.GetProperty("isNozzleLifted").GetBoolean());
            Assert.Equal(1, liftDoc.RootElement.GetProperty("pumpNumber").GetInt32());
        }

        // Verify lifted state in full state snapshot
        using HttpResponseMessage stateResponse1 = await client.GetAsync("/api/petronite/state");
        string stateBody1 = await stateResponse1.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse1.StatusCode);
        using (JsonDocument stateDoc1 = JsonDocument.Parse(stateBody1))
        {
            JsonElement pump1Assignment = FindNozzleAssignmentByPump(stateDoc1.RootElement.GetProperty("nozzleAssignments"), 1);
            Assert.True(pump1Assignment.GetProperty("isNozzleLifted").GetBoolean(),
                "Pump 1 nozzle should be lifted.");
        }

        // Set nozzle down on pump 1
        using HttpResponseMessage downResponse = await client.PostAsJsonAsync(
            "/api/petronite/set-nozzle-state",
            new { pumpNumber = 1, isNozzleLifted = false });
        string downBody = await downResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, downResponse.StatusCode);
        using (JsonDocument downDoc = JsonDocument.Parse(downBody))
        {
            Assert.False(downDoc.RootElement.GetProperty("isNozzleLifted").GetBoolean());
        }

        // Verify state changed
        using HttpResponseMessage stateResponse2 = await client.GetAsync("/api/petronite/state");
        string stateBody2 = await stateResponse2.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse2.StatusCode);
        using (JsonDocument stateDoc2 = JsonDocument.Parse(stateBody2))
        {
            JsonElement pump1Assignment = FindNozzleAssignmentByPump(stateDoc2.RootElement.GetProperty("nozzleAssignments"), 1);
            Assert.False(pump1Assignment.GetProperty("isNozzleLifted").GetBoolean(),
                "Pump 1 nozzle should no longer be lifted.");
        }
    }

    // ---------------------------------------------------------------------
    // 6. Reset Clears All State
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ResetClearsAllState_OrdersEmpty_WebhookUrlCleared_NozzleAssignmentsReset()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Set webhook URL
        using HttpResponseMessage setUrlResponse = await client.PostAsJsonAsync(
            "/api/petronite/set-webhook-url",
            new { url = "https://example.com/webhook/before-reset" });
        Assert.Equal(HttpStatusCode.OK, setUrlResponse.StatusCode);

        // Lift nozzle and inject a transaction
        using HttpResponseMessage liftResponse = await client.PostAsJsonAsync(
            "/api/petronite/set-nozzle-state",
            new { pumpNumber = 1, isNozzleLifted = true });
        Assert.Equal(HttpStatusCode.OK, liftResponse.StatusCode);

        using HttpResponseMessage injectResponse = await client.PostAsJsonAsync(
            "/api/petronite/inject-transaction",
            new { pumpNumber = 1, nozzleNumber = 1, amount = 30.00m });
        Assert.Equal(HttpStatusCode.Created, injectResponse.StatusCode);

        // Verify we have state before reset
        using HttpResponseMessage preResetState = await client.GetAsync("/api/petronite/state");
        string preResetBody = await preResetState.Content.ReadAsStringAsync();
        using (JsonDocument preResetDoc = JsonDocument.Parse(preResetBody))
        {
            Assert.Equal(1, preResetDoc.RootElement.GetProperty("orderCount").GetInt32());
            Assert.Equal("https://example.com/webhook/before-reset",
                preResetDoc.RootElement.GetProperty("webhookCallbackUrl").GetString());
        }

        // Reset simulator
        using HttpResponseMessage resetResponse = await client.PostAsync("/api/petronite/reset", null);
        string resetBody = await resetResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        using (JsonDocument resetDoc = JsonDocument.Parse(resetBody))
        {
            Assert.Equal(0, resetDoc.RootElement.GetProperty("orderCount").GetInt32());
            Assert.True(resetDoc.RootElement.GetProperty("nozzleCount").GetInt32() >= 4,
                "Nozzle assignments should be re-initialized after reset.");
        }

        // Verify state after reset
        using HttpResponseMessage postResetState = await client.GetAsync("/api/petronite/state");
        string postResetBody = await postResetState.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, postResetState.StatusCode);
        using JsonDocument postResetDoc = JsonDocument.Parse(postResetBody);
        JsonElement postResetRoot = postResetDoc.RootElement;

        // Orders should be empty
        Assert.Equal(0, postResetRoot.GetProperty("orderCount").GetInt32());
        Assert.Equal(0, postResetRoot.GetProperty("orders").GetArrayLength());

        // Webhook URL should be cleared
        Assert.True(
            postResetRoot.GetProperty("webhookCallbackUrl").ValueKind == JsonValueKind.Null
            || string.IsNullOrEmpty(postResetRoot.GetProperty("webhookCallbackUrl").GetString()),
            "Webhook URL should be cleared after reset.");

        // Nozzle assignments should be re-initialized (all nozzles down)
        JsonElement nozzleAssignments = postResetRoot.GetProperty("nozzleAssignments");
        Assert.True(nozzleAssignments.GetArrayLength() >= 4,
            "Nozzle assignments should be re-populated after reset.");

        foreach (JsonElement assignment in nozzleAssignments.EnumerateArray())
        {
            Assert.False(assignment.GetProperty("isNozzleLifted").GetBoolean(),
                $"Pump {assignment.GetProperty("pumpNumber").GetInt32()} nozzle should not be lifted after reset.");
        }

        // Active tokens should be cleared
        Assert.Equal(0, postResetRoot.GetProperty("activeTokenCount").GetInt32());
    }

    // ---------------------------------------------------------------------
    // 7. Multi-Pump Operations
    // ---------------------------------------------------------------------

    [Fact]
    public async Task MultiPumpOperations_InjectOnPumps1Through3_Verify3Orders_EachWithCorrectPump()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Lift nozzles on pumps 1, 2, and 3
        for (int pump = 1; pump <= 3; pump++)
        {
            using HttpResponseMessage liftResponse = await client.PostAsJsonAsync(
                "/api/petronite/set-nozzle-state",
                new { pumpNumber = pump, isNozzleLifted = true });
            Assert.Equal(HttpStatusCode.OK, liftResponse.StatusCode);
        }

        // Inject transactions on pumps 1, 2, and 3 with different amounts
        decimal[] amounts = [15.00m, 22.50m, 37.75m];
        for (int pump = 1; pump <= 3; pump++)
        {
            using HttpResponseMessage injectResponse = await client.PostAsJsonAsync(
                "/api/petronite/inject-transaction",
                new { pumpNumber = pump, nozzleNumber = 1, amount = amounts[pump - 1] });
            string injectBody = await injectResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Created, injectResponse.StatusCode);
            using JsonDocument injectDoc = JsonDocument.Parse(injectBody);
            Assert.Equal("completed", injectDoc.RootElement.GetProperty("status").GetString());
        }

        // Verify state shows 3 orders
        using HttpResponseMessage stateResponse = await client.GetAsync("/api/petronite/state");
        string stateBody = await stateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);
        using JsonDocument stateDoc = JsonDocument.Parse(stateBody);
        JsonElement stateRoot = stateDoc.RootElement;

        Assert.Equal(3, stateRoot.GetProperty("orderCount").GetInt32());

        JsonElement orders = stateRoot.GetProperty("orders");
        Assert.Equal(3, orders.GetArrayLength());

        // Verify each pump number is represented in the orders
        HashSet<int> orderPumpNumbers = new();
        foreach (JsonElement order in orders.EnumerateArray())
        {
            int pumpNumber = order.GetProperty("pumpNumber").GetInt32();
            orderPumpNumbers.Add(pumpNumber);
            Assert.Equal("Completed", order.GetProperty("status").GetString());
        }

        Assert.True(orderPumpNumbers.Contains(1), "Expected an order for pump 1.");
        Assert.True(orderPumpNumbers.Contains(2), "Expected an order for pump 2.");
        Assert.True(orderPumpNumbers.Contains(3), "Expected an order for pump 3.");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static JsonElement FindNozzleAssignmentByPump(JsonElement nozzleAssignments, int pumpNumber)
    {
        foreach (JsonElement assignment in nozzleAssignments.EnumerateArray())
        {
            if (assignment.GetProperty("pumpNumber").GetInt32() == pumpNumber)
            {
                return assignment;
            }
        }

        throw new InvalidOperationException($"No nozzle assignment found for pump {pumpNumber}.");
    }
}

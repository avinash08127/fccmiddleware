using System.Net;
using System.Text;
using System.Text.Json;

namespace VirtualLab.Tests.Simulators;

/// <summary>
/// TEST-5.1: VirtualLab TCP simulator E2E scenarios for the DOMS JPL simulator.
/// Validates the management API surface and simulator state management without
/// requiring direct TCP connections (which may conflict with CI port allocation).
/// </summary>
[Collection("Simulators")]
public sealed class DomsJplSimulatorE2ETests
{
    private readonly SimulatorTestFixture _fixture;

    public DomsJplSimulatorE2ETests(SimulatorTestFixture fixture)
    {
        _fixture = fixture;
    }

    // -----------------------------------------------------------------------
    // 1. Simulator Startup & State
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SimulatorStartsAndReportsCorrectInitialState()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        using HttpResponseMessage response = await client.GetAsync("/api/doms-jpl/state");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        // The simulator should be listening after startup.
        Assert.True(root.GetProperty("isListening").GetBoolean());

        // Default pump count is 4 (from DomsJplSimulatorOptions.PumpCount = 4).
        JsonElement pumpStates = root.GetProperty("pumpStates");
        int pumpCount = 0;
        foreach (JsonProperty pump in pumpStates.EnumerateObject())
        {
            pumpCount++;
            Assert.Equal("Idle", pump.Value.GetString());
        }

        Assert.Equal(4, pumpCount);

        // No transactions buffered initially.
        Assert.Equal(0, root.GetProperty("transactionCount").GetInt32());

        // No connected TCP clients (we are only using the management API).
        Assert.Equal(0, root.GetProperty("connectedClientCount").GetInt32());
    }

    // -----------------------------------------------------------------------
    // 2. Transaction Injection & Buffer
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InjectTransactionsIncreasesBufferCountAndResetClearsIt()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Inject 3 transactions with explicit details.
        using HttpResponseMessage inject1 = await PostJsonAsync(client, "/api/doms-jpl/inject-transaction",
            """{"transactionId":"TX-001","pumpNumber":1,"amount":50.00,"volume":12.50,"productCode":"UNL95"}""");
        using HttpResponseMessage inject2 = await PostJsonAsync(client, "/api/doms-jpl/inject-transaction",
            """{"transactionId":"TX-002","pumpNumber":2,"amount":80.00,"volume":20.00,"productCode":"DSL"}""");
        using HttpResponseMessage inject3 = await PostJsonAsync(client, "/api/doms-jpl/inject-transaction",
            """{"transactionId":"TX-003","pumpNumber":1,"amount":120.00,"volume":30.00,"productCode":"UNL98"}""");

        Assert.Equal(HttpStatusCode.Created, inject1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, inject2.StatusCode);
        Assert.Equal(HttpStatusCode.Created, inject3.StatusCode);

        // Verify each injection response contains the expected transactionId.
        using (JsonDocument doc1 = JsonDocument.Parse(await inject1.Content.ReadAsStringAsync()))
        {
            Assert.Equal("TX-001", doc1.RootElement.GetProperty("transactionId").GetString());
            Assert.Equal(1, doc1.RootElement.GetProperty("bufferCount").GetInt32());
        }

        using (JsonDocument doc3 = JsonDocument.Parse(await inject3.Content.ReadAsStringAsync()))
        {
            Assert.Equal("TX-003", doc3.RootElement.GetProperty("transactionId").GetString());
            Assert.Equal(3, doc3.RootElement.GetProperty("bufferCount").GetInt32());
        }

        // Verify state shows 3 buffered transactions.
        using HttpResponseMessage stateBeforeReset = await client.GetAsync("/api/doms-jpl/state");
        using (JsonDocument stateDoc = JsonDocument.Parse(await stateBeforeReset.Content.ReadAsStringAsync()))
        {
            Assert.Equal(3, stateDoc.RootElement.GetProperty("transactionCount").GetInt32());
        }

        // Reset the simulator.
        using HttpResponseMessage resetResponse = await PostJsonAsync(client, "/api/doms-jpl/reset", "{}");
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

        using (JsonDocument resetDoc = JsonDocument.Parse(await resetResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(0, resetDoc.RootElement.GetProperty("transactionCount").GetInt32());
        }

        // Verify state is cleared.
        using HttpResponseMessage stateAfterReset = await client.GetAsync("/api/doms-jpl/state");
        using (JsonDocument afterResetDoc = JsonDocument.Parse(await stateAfterReset.Content.ReadAsStringAsync()))
        {
            Assert.Equal(0, afterResetDoc.RootElement.GetProperty("transactionCount").GetInt32());
        }
    }

    // -----------------------------------------------------------------------
    // 3. Pump State Management
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SetPumpStateUpdatesSnapshotAndAcceptsValidTransitions()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Set pump 1 to Calling (by name).
        using HttpResponseMessage setCalling = await PostJsonAsync(client, "/api/doms-jpl/set-pump-state",
            """{"pumpNumber":1,"state":"Calling"}""");
        Assert.Equal(HttpStatusCode.OK, setCalling.StatusCode);

        using (JsonDocument callingDoc = JsonDocument.Parse(await setCalling.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Calling", callingDoc.RootElement.GetProperty("state").GetString());
            Assert.Equal(1, callingDoc.RootElement.GetProperty("stateCode").GetInt32());
        }

        // Set pump 2 to Fuelling (by numeric code "4").
        using HttpResponseMessage setFuelling = await PostJsonAsync(client, "/api/doms-jpl/set-pump-state",
            """{"pumpNumber":2,"state":"4"}""");
        Assert.Equal(HttpStatusCode.OK, setFuelling.StatusCode);

        using (JsonDocument fuellingDoc = JsonDocument.Parse(await setFuelling.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Fuelling", fuellingDoc.RootElement.GetProperty("state").GetString());
            Assert.Equal(4, fuellingDoc.RootElement.GetProperty("stateCode").GetInt32());
        }

        // Set pump 3 to EndOfTransaction.
        using HttpResponseMessage setEot = await PostJsonAsync(client, "/api/doms-jpl/set-pump-state",
            """{"pumpNumber":3,"state":"EndOfTransaction"}""");
        Assert.Equal(HttpStatusCode.OK, setEot.StatusCode);

        // Verify the full state snapshot reflects all changes.
        using HttpResponseMessage stateResponse = await client.GetAsync("/api/doms-jpl/state");
        using (JsonDocument stateDoc = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync()))
        {
            JsonElement pumpStates = stateDoc.RootElement.GetProperty("pumpStates");
            Assert.Equal("Calling", pumpStates.GetProperty("1").GetString());
            Assert.Equal("Fuelling", pumpStates.GetProperty("2").GetString());
            Assert.Equal("EndOfTransaction", pumpStates.GetProperty("3").GetString());
            Assert.Equal("Idle", pumpStates.GetProperty("4").GetString());
        }
    }

    // -----------------------------------------------------------------------
    // 4. Error Injection Configuration
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ErrorInjectionConfigurationAppliesAndResetClears()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Configure multiple error injection modes.
        using HttpResponseMessage injectError = await PostJsonAsync(client, "/api/doms-jpl/inject-error",
            """{"responseDelayMs":500,"sendMalformedFrame":true,"dropConnectionAfterLogon":false,"suppressHeartbeats":true,"rejectLogon":false,"rejectAuthorize":true,"shotCount":3}""");
        Assert.Equal(HttpStatusCode.OK, injectError.StatusCode);

        using (JsonDocument errorDoc = JsonDocument.Parse(await injectError.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Error injection configured.", errorDoc.RootElement.GetProperty("message").GetString());

            JsonElement injection = errorDoc.RootElement.GetProperty("injection");
            Assert.Equal(500, injection.GetProperty("responseDelayMs").GetInt32());
            Assert.True(injection.GetProperty("sendMalformedFrame").GetBoolean());
            Assert.False(injection.GetProperty("dropConnectionAfterLogon").GetBoolean());
            Assert.True(injection.GetProperty("suppressHeartbeats").GetBoolean());
            Assert.False(injection.GetProperty("rejectLogon").GetBoolean());
            Assert.True(injection.GetProperty("rejectAuthorize").GetBoolean());
            Assert.Equal(3, injection.GetProperty("shotCount").GetInt32());
        }

        // Verify error injection is visible in the state snapshot.
        using HttpResponseMessage stateResponse = await client.GetAsync("/api/doms-jpl/state");
        using (JsonDocument stateDoc = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync()))
        {
            JsonElement errorInjection = stateDoc.RootElement.GetProperty("errorInjection");
            Assert.Equal(500, errorInjection.GetProperty("responseDelayMs").GetInt32());
            Assert.True(errorInjection.GetProperty("sendMalformedFrame").GetBoolean());
            Assert.True(errorInjection.GetProperty("suppressHeartbeats").GetBoolean());
            Assert.True(errorInjection.GetProperty("rejectAuthorize").GetBoolean());
            Assert.Equal(3, errorInjection.GetProperty("shotCount").GetInt32());
        }

        // Reset the simulator -- error injection should be cleared.
        using HttpResponseMessage resetResponse = await PostJsonAsync(client, "/api/doms-jpl/reset", "{}");
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

        using HttpResponseMessage stateAfterReset = await client.GetAsync("/api/doms-jpl/state");
        using (JsonDocument afterResetDoc = JsonDocument.Parse(await stateAfterReset.Content.ReadAsStringAsync()))
        {
            JsonElement errorInjection = afterResetDoc.RootElement.GetProperty("errorInjection");
            Assert.Equal(0, errorInjection.GetProperty("responseDelayMs").GetInt32());
            Assert.False(errorInjection.GetProperty("sendMalformedFrame").GetBoolean());
            Assert.False(errorInjection.GetProperty("dropConnectionAfterLogon").GetBoolean());
            Assert.False(errorInjection.GetProperty("suppressHeartbeats").GetBoolean());
            Assert.False(errorInjection.GetProperty("rejectLogon").GetBoolean());
            Assert.False(errorInjection.GetProperty("rejectAuthorize").GetBoolean());
            Assert.Equal(0, errorInjection.GetProperty("shotCount").GetInt32());
        }
    }

    // -----------------------------------------------------------------------
    // 5. Pre-Auth Flow via Management API
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PreAuthFlowTransitionsPumpFromCallingToAuthorized()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Step 1: Set pump 2 to Calling.
        using HttpResponseMessage setCallingResponse = await PostJsonAsync(client, "/api/doms-jpl/set-pump-state",
            """{"pumpNumber":2,"state":"Calling"}""");
        Assert.Equal(HttpStatusCode.OK, setCallingResponse.StatusCode);

        // Step 2: Authorize pump 2.
        using HttpResponseMessage setAuthorizedResponse = await PostJsonAsync(client, "/api/doms-jpl/set-pump-state",
            """{"pumpNumber":2,"state":"Authorized"}""");
        Assert.Equal(HttpStatusCode.OK, setAuthorizedResponse.StatusCode);

        using (JsonDocument authDoc = JsonDocument.Parse(await setAuthorizedResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Authorized", authDoc.RootElement.GetProperty("state").GetString());
            Assert.Equal(2, authDoc.RootElement.GetProperty("stateCode").GetInt32());
        }

        // Step 3: Verify full state.
        using HttpResponseMessage finalState = await client.GetAsync("/api/doms-jpl/state");
        using (JsonDocument finalDoc = JsonDocument.Parse(await finalState.Content.ReadAsStringAsync()))
        {
            JsonElement pumpStates = finalDoc.RootElement.GetProperty("pumpStates");
            Assert.Equal("Idle", pumpStates.GetProperty("1").GetString());
            Assert.Equal("Authorized", pumpStates.GetProperty("2").GetString());
            Assert.Equal("Idle", pumpStates.GetProperty("3").GetString());
            Assert.Equal("Idle", pumpStates.GetProperty("4").GetString());
        }

        // Step 4: Full fuelling cycle: Started -> Fuelling -> EndOfTransaction.
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-state", """{"pumpNumber":2,"state":"Started"}""");
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-state", """{"pumpNumber":2,"state":"Fuelling"}""");
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-state", """{"pumpNumber":2,"state":"EndOfTransaction"}""");

        using HttpResponseMessage endState = await client.GetAsync("/api/doms-jpl/state");
        using (JsonDocument endDoc = JsonDocument.Parse(await endState.Content.ReadAsStringAsync()))
        {
            Assert.Equal("EndOfTransaction", endDoc.RootElement.GetProperty("pumpStates").GetProperty("2").GetString());
        }
    }

    // -----------------------------------------------------------------------
    // 6. Multi-Pump Independent States
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MultiplePumpsMaintainIndependentStatesSimultaneously()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Set 4 pumps to 4 different states.
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-state", """{"pumpNumber":1,"state":"Fuelling"}""");
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-state", """{"pumpNumber":2,"state":"Authorized"}""");
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-state", """{"pumpNumber":3,"state":"Offline"}""");
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-state", """{"pumpNumber":4,"state":"Calling"}""");

        // Verify each pump has its own independent state.
        using HttpResponseMessage stateResponse = await client.GetAsync("/api/doms-jpl/state");
        using (JsonDocument stateDoc = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync()))
        {
            JsonElement pumpStates = stateDoc.RootElement.GetProperty("pumpStates");
            Assert.Equal("Fuelling", pumpStates.GetProperty("1").GetString());
            Assert.Equal("Authorized", pumpStates.GetProperty("2").GetString());
            Assert.Equal("Offline", pumpStates.GetProperty("3").GetString());
            Assert.Equal("Calling", pumpStates.GetProperty("4").GetString());
        }

        // Change only pump 3 back to Idle -- others should remain unaffected.
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-state", """{"pumpNumber":3,"state":"Idle"}""");

        using HttpResponseMessage stateAfter = await client.GetAsync("/api/doms-jpl/state");
        using (JsonDocument partialDoc = JsonDocument.Parse(await stateAfter.Content.ReadAsStringAsync()))
        {
            JsonElement updatedPumpStates = partialDoc.RootElement.GetProperty("pumpStates");
            Assert.Equal("Fuelling", updatedPumpStates.GetProperty("1").GetString());
            Assert.Equal("Authorized", updatedPumpStates.GetProperty("2").GetString());
            Assert.Equal("Idle", updatedPumpStates.GetProperty("3").GetString());
            Assert.Equal("Calling", updatedPumpStates.GetProperty("4").GetString());
        }

        // Inject a transaction for pump 1 -- does not affect pump states.
        using HttpResponseMessage injectTx = await PostJsonAsync(client, "/api/doms-jpl/inject-transaction",
            """{"transactionId":"TX-MULTI-001","pumpNumber":1,"amount":75.00,"volume":18.75}""");
        Assert.Equal(HttpStatusCode.Created, injectTx.StatusCode);

        using HttpResponseMessage finalState = await client.GetAsync("/api/doms-jpl/state");
        using (JsonDocument finalDoc = JsonDocument.Parse(await finalState.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, finalDoc.RootElement.GetProperty("transactionCount").GetInt32());
        }
    }

    // -----------------------------------------------------------------------
    // Helper
    // -----------------------------------------------------------------------

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json)
    {
        return client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
    }
}

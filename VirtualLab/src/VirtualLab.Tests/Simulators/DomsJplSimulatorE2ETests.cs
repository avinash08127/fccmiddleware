using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VirtualLab.Infrastructure.DomsJpl;

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
    // 7. Unsolicited Push Delivery
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PushNotificationDeliversImmediateFrameToLoggedInTcpClients()
    {
        await _fixture.ResetAllSimulatorsAsync();

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(IPAddress.Loopback, 4001);
        await using NetworkStream stream = tcpClient.GetStream();

        await SendFrameAsync(
            stream,
            """{"type":"FcLogon_req","accessCode":"test-access-code","sequenceNumber":1}""");

        string logonResponse = await ReadFrameAsync(stream);
        using (JsonDocument logonDocument = JsonDocument.Parse(logonResponse))
        {
            Assert.Equal("FcLogon_resp", logonDocument.RootElement.GetProperty("type").GetString());
            Assert.Equal(0, logonDocument.RootElement.GetProperty("result").GetInt32());
        }

        using HttpResponseMessage pushResponse = await PostJsonAsync(
            _fixture.Client,
            "/api/doms-jpl/push-notification",
            """{"messageType":"FpStatusChange","pumpNumber":2,"state":"Calling"}""");

        Assert.Equal(HttpStatusCode.Accepted, pushResponse.StatusCode);

        string unsolicitedFrame = await ReadFrameAsync(stream);
        using JsonDocument pushDocument = JsonDocument.Parse(unsolicitedFrame);
        Assert.Equal("FpStatusChange", pushDocument.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, pushDocument.RootElement.GetProperty("pumpNumber").GetInt32());
        Assert.Equal("Calling", pushDocument.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, pushDocument.RootElement.GetProperty("stateCode").GetInt32());
    }

    // -----------------------------------------------------------------------
    // 8. Pump Control via TCP
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FpEmergencyStop_SetsPumpToErrorState()
    {
        await _fixture.ResetAllSimulatorsAsync();

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(IPAddress.Loopback, 4001);
        await using NetworkStream stream = tcpClient.GetStream();

        // Log in first.
        await SendFrameAsync(stream, """{"type":"FcLogon_req","accessCode":"test-access-code","sequenceNumber":1}""");
        string logonResp = await ReadFrameAsync(stream);
        using (JsonDocument ld = JsonDocument.Parse(logonResp))
        {
            Assert.Equal(0, ld.RootElement.GetProperty("result").GetInt32());
        }

        // Send emergency stop for pump 2.
        await SendFrameAsync(stream, """{"type":"FpEmergencyStop_req","pumpNumber":2,"sequenceNumber":2}""");
        string stopResp = await ReadFrameAsync(stream);

        using JsonDocument stopDoc = JsonDocument.Parse(stopResp);
        Assert.Equal("FpEmergencyStop_resp", stopDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, stopDoc.RootElement.GetProperty("result").GetInt32());
        Assert.Equal(2, stopDoc.RootElement.GetProperty("pumpNumber").GetInt32());

        // Verify pump 2 is now in Error state via management API.
        using HttpResponseMessage stateResp = await _fixture.Client.GetAsync("/api/doms-jpl/state");
        using JsonDocument stateDoc = JsonDocument.Parse(await stateResp.Content.ReadAsStringAsync());
        Assert.Equal("Error", stateDoc.RootElement.GetProperty("pumpStates").GetProperty("2").GetString());
    }

    [Fact]
    public async Task FpCancelEmergencyStop_SetsPumpToIdleState()
    {
        await _fixture.ResetAllSimulatorsAsync();

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(IPAddress.Loopback, 4001);
        await using NetworkStream stream = tcpClient.GetStream();

        await SendFrameAsync(stream, """{"type":"FcLogon_req","accessCode":"test-access-code","sequenceNumber":1}""");
        string logonResp = await ReadFrameAsync(stream);
        using (JsonDocument ld = JsonDocument.Parse(logonResp))
        {
            Assert.Equal(0, ld.RootElement.GetProperty("result").GetInt32());
        }

        // First set pump 3 to Error via emergency stop.
        await SendFrameAsync(stream, """{"type":"FpEmergencyStop_req","pumpNumber":3,"sequenceNumber":2}""");
        await ReadFrameAsync(stream);

        // Then cancel the emergency stop.
        await SendFrameAsync(stream, """{"type":"FpCancelEmergencyStop_req","pumpNumber":3,"sequenceNumber":3}""");
        string cancelResp = await ReadFrameAsync(stream);

        using JsonDocument cancelDoc = JsonDocument.Parse(cancelResp);
        Assert.Equal("FpCancelEmergencyStop_resp", cancelDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, cancelDoc.RootElement.GetProperty("result").GetInt32());
        Assert.Equal(3, cancelDoc.RootElement.GetProperty("pumpNumber").GetInt32());

        // Verify pump 3 is back to Idle.
        using HttpResponseMessage stateResp = await _fixture.Client.GetAsync("/api/doms-jpl/state");
        using JsonDocument stateDoc = JsonDocument.Parse(await stateResp.Content.ReadAsStringAsync());
        Assert.Equal("Idle", stateDoc.RootElement.GetProperty("pumpStates").GetProperty("3").GetString());
    }

    [Fact]
    public async Task FpClose_SetsPumpToClosedState()
    {
        await _fixture.ResetAllSimulatorsAsync();

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(IPAddress.Loopback, 4001);
        await using NetworkStream stream = tcpClient.GetStream();

        await SendFrameAsync(stream, """{"type":"FcLogon_req","accessCode":"test-access-code","sequenceNumber":1}""");
        string logonResp = await ReadFrameAsync(stream);
        using (JsonDocument ld = JsonDocument.Parse(logonResp))
        {
            Assert.Equal(0, ld.RootElement.GetProperty("result").GetInt32());
        }

        // Close pump 1.
        await SendFrameAsync(stream, """{"type":"FpClose_req","pumpNumber":1,"sequenceNumber":2}""");
        string closeResp = await ReadFrameAsync(stream);

        using JsonDocument closeDoc = JsonDocument.Parse(closeResp);
        Assert.Equal("FpClose_resp", closeDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, closeDoc.RootElement.GetProperty("result").GetInt32());
        Assert.Equal(1, closeDoc.RootElement.GetProperty("pumpNumber").GetInt32());

        // Verify pump 1 is now Closed.
        using HttpResponseMessage stateResp = await _fixture.Client.GetAsync("/api/doms-jpl/state");
        using JsonDocument stateDoc = JsonDocument.Parse(await stateResp.Content.ReadAsStringAsync());
        Assert.Equal("Closed", stateDoc.RootElement.GetProperty("pumpStates").GetProperty("1").GetString());
    }

    [Fact]
    public async Task FpOpen_SetsPumpToIdleState()
    {
        await _fixture.ResetAllSimulatorsAsync();

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(IPAddress.Loopback, 4001);
        await using NetworkStream stream = tcpClient.GetStream();

        await SendFrameAsync(stream, """{"type":"FcLogon_req","accessCode":"test-access-code","sequenceNumber":1}""");
        string logonResp = await ReadFrameAsync(stream);
        using (JsonDocument ld = JsonDocument.Parse(logonResp))
        {
            Assert.Equal(0, ld.RootElement.GetProperty("result").GetInt32());
        }

        // First close pump 4.
        await SendFrameAsync(stream, """{"type":"FpClose_req","pumpNumber":4,"sequenceNumber":2}""");
        await ReadFrameAsync(stream);

        // Then re-open pump 4.
        await SendFrameAsync(stream, """{"type":"FpOpen_req","pumpNumber":4,"sequenceNumber":3}""");
        string openResp = await ReadFrameAsync(stream);

        using JsonDocument openDoc = JsonDocument.Parse(openResp);
        Assert.Equal("FpOpen_resp", openDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, openDoc.RootElement.GetProperty("result").GetInt32());
        Assert.Equal(4, openDoc.RootElement.GetProperty("pumpNumber").GetInt32());

        // Verify pump 4 is back to Idle.
        using HttpResponseMessage stateResp = await _fixture.Client.GetAsync("/api/doms-jpl/state");
        using JsonDocument stateDoc = JsonDocument.Parse(await stateResp.Content.ReadAsStringAsync());
        Assert.Equal("Idle", stateDoc.RootElement.GetProperty("pumpStates").GetProperty("4").GetString());
    }

    // -----------------------------------------------------------------------
    // 9. Price Management via Management API
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SetPricesEndpointUpdatesPricesVisibleInStateSnapshot()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Set new prices for two grades.
        using HttpResponseMessage setPriceResp = await PostJsonAsync(client, "/api/doms-jpl/set-prices",
            """{"grades":[{"gradeId":"01","gradeName":"UNL95","priceMinorUnits":5200,"currencyCode":"TRY"},{"gradeId":"03","gradeName":"LPG","priceMinorUnits":3100,"currencyCode":"TRY"}]}""");
        Assert.Equal(HttpStatusCode.OK, setPriceResp.StatusCode);

        using (JsonDocument priceDoc = JsonDocument.Parse(await setPriceResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Price set updated.", priceDoc.RootElement.GetProperty("message").GetString());
        }

        // Verify via state snapshot.
        using HttpResponseMessage stateResp = await client.GetAsync("/api/doms-jpl/state");
        using JsonDocument stateDoc = JsonDocument.Parse(await stateResp.Content.ReadAsStringAsync());
        JsonElement priceSet = stateDoc.RootElement.GetProperty("priceSet");
        JsonElement gradePrices = priceSet.GetProperty("gradePrices");

        // Grade 01 should be updated.
        Assert.Equal(5200, gradePrices.GetProperty("01").GetProperty("priceMinorUnits").GetInt64());
        Assert.Equal("UNL95", gradePrices.GetProperty("01").GetProperty("gradeName").GetString());

        // Grade 03 should be newly added.
        Assert.Equal(3100, gradePrices.GetProperty("03").GetProperty("priceMinorUnits").GetInt64());
        Assert.Equal("LPG", gradePrices.GetProperty("03").GetProperty("gradeName").GetString());

        // Grade 02 (default DIESEL) should remain unchanged.
        Assert.Equal(5000, gradePrices.GetProperty("02").GetProperty("priceMinorUnits").GetInt64());
    }

    // -----------------------------------------------------------------------
    // 10. Price Management via TCP
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FcPriceSet_ReturnsCurrentPricesViaTcp()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Set a known price via management API first.
        await PostJsonAsync(client, "/api/doms-jpl/set-prices",
            """{"grades":[{"gradeId":"01","gradeName":"UNL95","priceMinorUnits":6000,"currencyCode":"TRY"}]}""");

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(IPAddress.Loopback, 4001);
        await using NetworkStream stream = tcpClient.GetStream();

        await SendFrameAsync(stream, """{"type":"FcLogon_req","accessCode":"test-access-code","sequenceNumber":1}""");
        await ReadFrameAsync(stream);

        // Request the price set via TCP.
        await SendFrameAsync(stream, """{"type":"FcPriceSet_req","sequenceNumber":2}""");
        string priceResp = await ReadFrameAsync(stream);

        using JsonDocument priceDoc = JsonDocument.Parse(priceResp);
        Assert.Equal("FcPriceSet_resp", priceDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, priceDoc.RootElement.GetProperty("result").GetInt32());

        // Verify the grade count and that grade 01 has the updated price.
        JsonElement grades = priceDoc.RootElement.GetProperty("grades");
        bool foundGrade01 = false;
        foreach (JsonElement grade in grades.EnumerateArray())
        {
            if (grade.GetProperty("gradeId").GetString() == "01")
            {
                Assert.Equal(6000, grade.GetProperty("priceMinorUnits").GetInt64());
                foundGrade01 = true;
            }
        }
        Assert.True(foundGrade01, "Grade 01 should be present in the FcPriceSet_resp.");
    }

    [Fact]
    public async Task FcPriceUpdate_UpdatesPricesViaTcp()
    {
        await _fixture.ResetAllSimulatorsAsync();

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(IPAddress.Loopback, 4001);
        await using NetworkStream stream = tcpClient.GetStream();

        await SendFrameAsync(stream, """{"type":"FcLogon_req","accessCode":"test-access-code","sequenceNumber":1}""");
        await ReadFrameAsync(stream);

        // Update grade 02 price via TCP.
        await SendFrameAsync(stream,
            """{"type":"FcPriceUpdate_req","sequenceNumber":2,"grades":[{"gradeId":"02","priceMinorUnits":7777}]}""");
        string updateResp = await ReadFrameAsync(stream);

        using JsonDocument updateDoc = JsonDocument.Parse(updateResp);
        Assert.Equal("FcPriceUpdate_resp", updateDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, updateDoc.RootElement.GetProperty("result").GetInt32());

        // Verify the price change is reflected in the management API state snapshot.
        using HttpResponseMessage stateResp = await _fixture.Client.GetAsync("/api/doms-jpl/state");
        using JsonDocument stateDoc = JsonDocument.Parse(await stateResp.Content.ReadAsStringAsync());
        JsonElement gradePrices = stateDoc.RootElement.GetProperty("priceSet").GetProperty("gradePrices");
        Assert.Equal(7777, gradePrices.GetProperty("02").GetProperty("priceMinorUnits").GetInt64());
    }

    // -----------------------------------------------------------------------
    // 11. Unsupervised Transaction Injection & Retrieval
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InjectUnsupervisedTransaction_AppearsInStateSnapshot()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Inject two unsupervised transactions.
        using HttpResponseMessage inject1 = await PostJsonAsync(client, "/api/doms-jpl/inject-unsupervised-transaction",
            """{"transactionId":"UNSUP-001","pumpNumber":1,"amount":30.00,"volume":7.50,"productCode":"UNL95"}""");
        using HttpResponseMessage inject2 = await PostJsonAsync(client, "/api/doms-jpl/inject-unsupervised-transaction",
            """{"transactionId":"UNSUP-002","pumpNumber":2,"amount":60.00,"volume":15.00,"productCode":"DSL"}""");

        Assert.Equal(HttpStatusCode.Created, inject1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, inject2.StatusCode);

        using (JsonDocument doc1 = JsonDocument.Parse(await inject1.Content.ReadAsStringAsync()))
        {
            Assert.Equal("UNSUP-001", doc1.RootElement.GetProperty("transactionId").GetString());
            Assert.Equal(1, doc1.RootElement.GetProperty("bufferCount").GetInt32());
        }

        using (JsonDocument doc2 = JsonDocument.Parse(await inject2.Content.ReadAsStringAsync()))
        {
            Assert.Equal("UNSUP-002", doc2.RootElement.GetProperty("transactionId").GetString());
            Assert.Equal(2, doc2.RootElement.GetProperty("bufferCount").GetInt32());
        }

        // Verify state snapshot shows the unsupervised transaction count.
        using HttpResponseMessage stateResp = await client.GetAsync("/api/doms-jpl/state");
        using JsonDocument stateDoc = JsonDocument.Parse(await stateResp.Content.ReadAsStringAsync());
        Assert.Equal(2, stateDoc.RootElement.GetProperty("unsupervisedTransactionCount").GetInt32());

        // The regular transaction buffer should be unaffected.
        Assert.Equal(0, stateDoc.RootElement.GetProperty("transactionCount").GetInt32());
    }

    [Fact]
    public async Task FpUnsupTransRead_ReturnsUnsupervisedTransactionsViaTcp()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Inject an unsupervised transaction via management API.
        await PostJsonAsync(client, "/api/doms-jpl/inject-unsupervised-transaction",
            """{"transactionId":"UNSUP-TCP-001","pumpNumber":3,"amount":45.00,"volume":11.25,"productCode":"UNL98"}""");

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(IPAddress.Loopback, 4001);
        await using NetworkStream stream = tcpClient.GetStream();

        await SendFrameAsync(stream, """{"type":"FcLogon_req","accessCode":"test-access-code","sequenceNumber":1}""");
        await ReadFrameAsync(stream);

        // Read unsupervised transactions via TCP.
        await SendFrameAsync(stream, """{"type":"FpUnsupTrans_read_req","pumpNumber":3,"sequenceNumber":2}""");
        string readResp = await ReadFrameAsync(stream);

        using JsonDocument readDoc = JsonDocument.Parse(readResp);
        Assert.Equal("FpUnsupTrans_read_resp", readDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, readDoc.RootElement.GetProperty("result").GetInt32());
        Assert.Equal(1, readDoc.RootElement.GetProperty("transactionCount").GetInt32());

        JsonElement transactions = readDoc.RootElement.GetProperty("transactions");
        JsonElement firstTx = transactions[0];
        Assert.Equal("UNSUP-TCP-001", firstTx.GetProperty("transactionId").GetString());
        Assert.Equal(3, firstTx.GetProperty("pumpNumber").GetInt32());
        Assert.Equal(45.00m, firstTx.GetProperty("amount").GetDecimal());

        // After reading, the unsupervised buffer should be cleared.
        using HttpResponseMessage stateResp = await client.GetAsync("/api/doms-jpl/state");
        using JsonDocument stateDoc = JsonDocument.Parse(await stateResp.Content.ReadAsStringAsync());
        Assert.Equal(0, stateDoc.RootElement.GetProperty("unsupervisedTransactionCount").GetInt32());
    }

    // -----------------------------------------------------------------------
    // 12. Peripheral Push Endpoints
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PushBnaReport_ReturnsAccepted()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        using HttpResponseMessage resp = await PostJsonAsync(client, "/api/doms-jpl/push-bna-report",
            """{"terminalId":"BNA-TEST-01","notesAccepted":5}""");

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("BNA report pushed to active clients.", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("BNA-TEST-01", doc.RootElement.GetProperty("payload").GetProperty("terminalId").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("payload").GetProperty("notesAccepted").GetInt32());
    }

    [Fact]
    public async Task PushDispenserInstall_ReturnsAccepted()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        using HttpResponseMessage resp = await PostJsonAsync(client, "/api/doms-jpl/push-dispenser-install",
            """{"dispenserId":"DISP-TEST-01","model":"Wayne Helix 5000"}""");

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Dispenser install data pushed to active clients.", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("DISP-TEST-01", doc.RootElement.GetProperty("payload").GetProperty("dispenserId").GetString());
        Assert.Equal("Wayne Helix 5000", doc.RootElement.GetProperty("payload").GetProperty("model").GetString());
    }

    [Fact]
    public async Task PushEptInfo_ReturnsAccepted()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        using HttpResponseMessage resp = await PostJsonAsync(client, "/api/doms-jpl/push-ept-info",
            """{"terminalId":"EPT-TEST-01","version":"2.3.1"}""");

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("EPT info pushed to active clients.", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("EPT-TEST-01", doc.RootElement.GetProperty("payload").GetProperty("terminalId").GetString());
        Assert.Equal("2.3.1", doc.RootElement.GetProperty("payload").GetProperty("version").GetString());
    }

    // -----------------------------------------------------------------------
    // 13. Pump Totals via Management API
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SetPumpTotalsEndpointUpdatesTotalsVisibleInStateSnapshot()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Set totals for pump 1.
        using HttpResponseMessage setResp = await PostJsonAsync(client, "/api/doms-jpl/set-pump-totals",
            """{"pumpNumber":1,"totalVolumeMicrolitres":123456789,"totalAmountMinorUnits":987654}""");
        Assert.Equal(HttpStatusCode.OK, setResp.StatusCode);

        using (JsonDocument setDoc = JsonDocument.Parse(await setResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Pump 1 totals set.", setDoc.RootElement.GetProperty("message").GetString());
            Assert.Equal(1, setDoc.RootElement.GetProperty("pumpNumber").GetInt32());
            Assert.Equal(123456789, setDoc.RootElement.GetProperty("totalVolumeMicrolitres").GetInt64());
            Assert.Equal(987654, setDoc.RootElement.GetProperty("totalAmountMinorUnits").GetInt64());
        }

        // Set totals for pump 2 as well.
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-totals",
            """{"pumpNumber":2,"totalVolumeMicrolitres":50000000,"totalAmountMinorUnits":200000}""");

        // Verify via state snapshot.
        using HttpResponseMessage stateResp = await client.GetAsync("/api/doms-jpl/state");
        using JsonDocument stateDoc = JsonDocument.Parse(await stateResp.Content.ReadAsStringAsync());
        JsonElement pumpTotals = stateDoc.RootElement.GetProperty("pumpTotals");

        Assert.Equal(123456789, pumpTotals.GetProperty("1").GetProperty("totalVolumeMicrolitres").GetInt64());
        Assert.Equal(987654, pumpTotals.GetProperty("1").GetProperty("totalAmountMinorUnits").GetInt64());
        Assert.Equal(50000000, pumpTotals.GetProperty("2").GetProperty("totalVolumeMicrolitres").GetInt64());
        Assert.Equal(200000, pumpTotals.GetProperty("2").GetProperty("totalAmountMinorUnits").GetInt64());
    }

    // -----------------------------------------------------------------------
    // 14. Pump Totals via TCP
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FpTotals_ReturnsCorrectTotalsViaTcp()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Set known totals via management API.
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-totals",
            """{"pumpNumber":2,"totalVolumeMicrolitres":333000000,"totalAmountMinorUnits":1500000}""");

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(IPAddress.Loopback, 4001);
        await using NetworkStream stream = tcpClient.GetStream();

        await SendFrameAsync(stream, """{"type":"FcLogon_req","accessCode":"test-access-code","sequenceNumber":1}""");
        await ReadFrameAsync(stream);

        // Request totals for pump 2 via TCP.
        await SendFrameAsync(stream, """{"type":"FpTotals_req","pumpNumber":2,"sequenceNumber":2}""");
        string totalsResp = await ReadFrameAsync(stream);

        using JsonDocument totalsDoc = JsonDocument.Parse(totalsResp);
        Assert.Equal("FpTotals_resp", totalsDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, totalsDoc.RootElement.GetProperty("result").GetInt32());
        Assert.Equal(1, totalsDoc.RootElement.GetProperty("pumpCount").GetInt32());

        JsonElement totals = totalsDoc.RootElement.GetProperty("totals");
        JsonElement pump2Totals = totals[0];
        Assert.Equal(2, pump2Totals.GetProperty("pumpNumber").GetInt32());
        Assert.Equal(333000000, pump2Totals.GetProperty("totalVolumeMicrolitres").GetInt64());
        Assert.Equal(1500000, pump2Totals.GetProperty("totalAmountMinorUnits").GetInt64());
    }

    [Fact]
    public async Task FpTotals_ReturnsAllPumpTotalsWhenNoPumpSpecified()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Set totals for pumps 1 and 3.
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-totals",
            """{"pumpNumber":1,"totalVolumeMicrolitres":100000,"totalAmountMinorUnits":5000}""");
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-totals",
            """{"pumpNumber":3,"totalVolumeMicrolitres":200000,"totalAmountMinorUnits":10000}""");

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(IPAddress.Loopback, 4001);
        await using NetworkStream stream = tcpClient.GetStream();

        await SendFrameAsync(stream, """{"type":"FcLogon_req","accessCode":"test-access-code","sequenceNumber":1}""");
        await ReadFrameAsync(stream);

        // Request all totals (pumpNumber=0 means all).
        await SendFrameAsync(stream, """{"type":"FpTotals_req","pumpNumber":0,"sequenceNumber":2}""");
        string totalsResp = await ReadFrameAsync(stream);

        using JsonDocument totalsDoc = JsonDocument.Parse(totalsResp);
        Assert.Equal("FpTotals_resp", totalsDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, totalsDoc.RootElement.GetProperty("result").GetInt32());

        // Should return totals for all 4 pumps (initialized at reset).
        Assert.Equal(4, totalsDoc.RootElement.GetProperty("pumpCount").GetInt32());
    }

    // -----------------------------------------------------------------------
    // 15. Reset Clears Phase 7 State
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResetClearsUnsupervisedTransactionsPricesTotals()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Inject unsupervised transactions.
        await PostJsonAsync(client, "/api/doms-jpl/inject-unsupervised-transaction",
            """{"transactionId":"UNSUP-RESET-001","pumpNumber":1,"amount":20.00,"volume":5.00}""");
        await PostJsonAsync(client, "/api/doms-jpl/inject-unsupervised-transaction",
            """{"transactionId":"UNSUP-RESET-002","pumpNumber":2,"amount":40.00,"volume":10.00}""");

        // Update prices.
        await PostJsonAsync(client, "/api/doms-jpl/set-prices",
            """{"grades":[{"gradeId":"01","gradeName":"UNL95","priceMinorUnits":9999,"currencyCode":"TRY"}]}""");

        // Update pump totals.
        await PostJsonAsync(client, "/api/doms-jpl/set-pump-totals",
            """{"pumpNumber":1,"totalVolumeMicrolitres":999999,"totalAmountMinorUnits":888888}""");

        // Verify state has the injected data.
        using HttpResponseMessage preResetState = await client.GetAsync("/api/doms-jpl/state");
        using (JsonDocument preDoc = JsonDocument.Parse(await preResetState.Content.ReadAsStringAsync()))
        {
            Assert.Equal(2, preDoc.RootElement.GetProperty("unsupervisedTransactionCount").GetInt32());
            Assert.Equal(9999, preDoc.RootElement.GetProperty("priceSet")
                .GetProperty("gradePrices").GetProperty("01").GetProperty("priceMinorUnits").GetInt64());
            Assert.Equal(999999, preDoc.RootElement.GetProperty("pumpTotals")
                .GetProperty("1").GetProperty("totalVolumeMicrolitres").GetInt64());
        }

        // Reset the simulator.
        using HttpResponseMessage resetResp = await PostJsonAsync(client, "/api/doms-jpl/reset", "{}");
        Assert.Equal(HttpStatusCode.OK, resetResp.StatusCode);

        // Verify unsupervised transactions are cleared.
        using HttpResponseMessage postResetState = await client.GetAsync("/api/doms-jpl/state");
        using JsonDocument postDoc = JsonDocument.Parse(await postResetState.Content.ReadAsStringAsync());

        Assert.Equal(0, postDoc.RootElement.GetProperty("unsupervisedTransactionCount").GetInt32());

        // Verify prices are reset to defaults (grade 01 = 4500, grade 02 = 5000).
        JsonElement resetPrices = postDoc.RootElement.GetProperty("priceSet").GetProperty("gradePrices");
        Assert.Equal(4500, resetPrices.GetProperty("01").GetProperty("priceMinorUnits").GetInt64());
        Assert.Equal(5000, resetPrices.GetProperty("02").GetProperty("priceMinorUnits").GetInt64());

        // Verify pump totals are zeroed out.
        JsonElement resetTotals = postDoc.RootElement.GetProperty("pumpTotals");
        Assert.Equal(0, resetTotals.GetProperty("1").GetProperty("totalVolumeMicrolitres").GetInt64());
        Assert.Equal(0, resetTotals.GetProperty("1").GetProperty("totalAmountMinorUnits").GetInt64());

        // Verify all pumps are back to Idle.
        JsonElement resetPumpStates = postDoc.RootElement.GetProperty("pumpStates");
        for (int i = 1; i <= 4; i++)
        {
            Assert.Equal("Idle", resetPumpStates.GetProperty(i.ToString()).GetString());
        }
    }

    // -----------------------------------------------------------------------
    // Helper
    // -----------------------------------------------------------------------

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json)
    {
        return client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static async Task SendFrameAsync(NetworkStream stream, string payload)
    {
        byte[] frame = DomsJplFrameCodec.Encode(payload);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static async Task<string> ReadFrameAsync(NetworkStream stream, int timeoutMs = 5000)
    {
        using CancellationTokenSource timeout = new(timeoutMs);
        List<byte> buffer = [];
        byte[] readBuffer = new byte[4096];

        while (!timeout.Token.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), timeout.Token);
            if (read == 0)
            {
                throw new IOException("The DOMS simulator closed the TCP connection before a frame was received.");
            }

            buffer.AddRange(readBuffer.AsSpan(0, read).ToArray());

            int stxIndex = buffer.IndexOf(DomsJplFrameCodec.Stx);
            int etxIndex = buffer.IndexOf(DomsJplFrameCodec.Etx);
            if (stxIndex >= 0 && etxIndex > stxIndex)
            {
                byte[] payload = buffer.Skip(stxIndex + 1).Take(etxIndex - stxIndex - 1).ToArray();
                return Encoding.UTF8.GetString(payload);
            }
        }

        throw new TimeoutException("Timed out waiting for a DOMS JPL frame.");
    }
}

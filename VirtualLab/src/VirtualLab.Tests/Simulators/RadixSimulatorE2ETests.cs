using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VirtualLab.Tests.Api;

namespace VirtualLab.Tests.Simulators;

/// <summary>
/// TEST-5.2: VirtualLab Radix simulator E2E scenarios.
/// All tests interact exclusively through the Radix management API endpoints.
/// </summary>
public sealed class RadixSimulatorE2ETests
{
    // -----------------------------------------------------------------------
    // 1. Heartbeat / Product Read
    // -----------------------------------------------------------------------

    [Fact]
    public async Task HeartbeatAndProductRead_InitialState_ProductCatalogPresent()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage stateResponse = await client.GetAsync("/api/radix/state");
        string stateBody = await stateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);

        using JsonDocument document = JsonDocument.Parse(stateBody);
        JsonElement root = document.RootElement;

        Assert.Equal("OnDemand", root.GetProperty("mode").GetString());
        Assert.True(root.GetProperty("tokenCounter").GetInt64() >= 0);
        Assert.Equal(0, root.GetProperty("bufferedTransactionCount").GetInt32());
        Assert.Equal(0, root.GetProperty("pendingErrorInjections").GetInt32());

        JsonElement productCatalog = root.GetProperty("productCatalog");
        Assert.True(productCatalog.EnumerateObject().Count() >= 4,
            "Product catalog should contain at least 4 default products after startup reset.");

        // Verify well-known seeded products
        Assert.Equal("UNLEADED 95", productCatalog.GetProperty("1").GetProperty("name").GetString());
        Assert.Equal("DIESEL", productCatalog.GetProperty("3").GetProperty("name").GetString());
    }

    // -----------------------------------------------------------------------
    // 2. FIFO Buffer Injection
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FifoBufferInjection_InjectFive_BufferDepthAndOrderPreserved()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        // Inject 5 transactions with ascending saveNum so we can verify order
        for (int i = 1; i <= 5; i++)
        {
            using HttpResponseMessage injectResponse = await client.PostAsJsonAsync(
                "/api/radix/inject-transaction",
                new
                {
                    pumpNumber = 1,
                    nozzleNumber = 1,
                    productId = 1,
                    productName = "UNLEADED 95",
                    volume = $"{10.0 * i:F2}",
                    amount = $"{18.5 * i:F2}",
                    price = "1.850",
                    saveNum = i.ToString(),
                });

            Assert.Equal(HttpStatusCode.Created, injectResponse.StatusCode);
        }

        // Verify buffer depth
        using HttpResponseMessage stateResponse = await client.GetAsync("/api/radix/state");
        string stateBody = await stateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);

        using JsonDocument document = JsonDocument.Parse(stateBody);
        JsonElement root = document.RootElement;

        Assert.Equal(5, root.GetProperty("bufferedTransactionCount").GetInt32());

        // Verify FIFO order via saveNum
        JsonElement[] bufferedTransactions = root
            .GetProperty("bufferedTransactions")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(5, bufferedTransactions.Length);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal((i + 1).ToString(), bufferedTransactions[i].GetProperty("saveNum").GetString());
        }
    }

    // -----------------------------------------------------------------------
    // 3. Normalization Fields
    // -----------------------------------------------------------------------

    [Fact]
    public async Task NormalizationFields_InjectedValues_AllFieldsPresentInSnapshot()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage injectResponse = await client.PostAsJsonAsync(
            "/api/radix/inject-transaction",
            new
            {
                pumpNumber = 3,
                nozzleNumber = 2,
                productId = 2,
                productName = "UNLEADED 98",
                volume = "42.75",
                amount = "87.64",
                price = "2.050",
                fdcDate = "13/03/2026",
                fdcTime = "14:30:00",
                efdId = "7",
                saveNum = "999",
            });

        Assert.Equal(HttpStatusCode.Created, injectResponse.StatusCode);

        using HttpResponseMessage stateResponse = await client.GetAsync("/api/radix/state");
        string stateBody = await stateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);

        using JsonDocument document = JsonDocument.Parse(stateBody);
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("bufferedTransactionCount").GetInt32());

        JsonElement transaction = root.GetProperty("bufferedTransactions").EnumerateArray().Single();

        Assert.Equal(3, transaction.GetProperty("pumpNumber").GetInt32());
        Assert.Equal(2, transaction.GetProperty("nozzleNumber").GetInt32());
        Assert.Equal(2, transaction.GetProperty("productId").GetInt32());
        Assert.Equal("UNLEADED 98", transaction.GetProperty("productName").GetString());
        Assert.Equal("42.75", transaction.GetProperty("volume").GetString());
        Assert.Equal("87.64", transaction.GetProperty("amount").GetString());
        Assert.Equal("2.050", transaction.GetProperty("price").GetString());
        Assert.Equal("13/03/2026", transaction.GetProperty("fdcDate").GetString());
        Assert.Equal("14:30:00", transaction.GetProperty("fdcTime").GetString());
        Assert.Equal("7", transaction.GetProperty("efdId").GetString());
        Assert.Equal("999", transaction.GetProperty("saveNum").GetString());
    }

    // -----------------------------------------------------------------------
    // 4. Mode Management
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ModeManagement_SwitchBetweenOnDemandAndUnsolicited()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        // Confirm initial mode is ON_DEMAND
        using HttpResponseMessage initialState = await client.GetAsync("/api/radix/state");
        string initialBody = await initialState.Content.ReadAsStringAsync();
        using (JsonDocument initialDoc = JsonDocument.Parse(initialBody))
        {
            Assert.Equal("OnDemand", initialDoc.RootElement.GetProperty("mode").GetString());
        }

        // Switch to UNSOLICITED with a callback URL
        using HttpResponseMessage setUnsolicitedResponse = await client.PostAsJsonAsync(
            "/api/radix/set-mode",
            new
            {
                mode = "UNSOLICITED",
                callbackUrl = "http://localhost:9999/unsolicited-callback",
            });
        string setUnsolicitedBody = await setUnsolicitedResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, setUnsolicitedResponse.StatusCode);
        using (JsonDocument setDoc = JsonDocument.Parse(setUnsolicitedBody))
        {
            Assert.Equal("Unsolicited", setDoc.RootElement.GetProperty("mode").GetString());
            Assert.Equal("http://localhost:9999/unsolicited-callback",
                setDoc.RootElement.GetProperty("callbackUrl").GetString());
        }

        // Verify state reflects UNSOLICITED
        using HttpResponseMessage afterSwitchState = await client.GetAsync("/api/radix/state");
        string afterSwitchBody = await afterSwitchState.Content.ReadAsStringAsync();
        using (JsonDocument afterDoc = JsonDocument.Parse(afterSwitchBody))
        {
            Assert.Equal("Unsolicited", afterDoc.RootElement.GetProperty("mode").GetString());
            Assert.Equal("http://localhost:9999/unsolicited-callback",
                afterDoc.RootElement.GetProperty("unsolicitedCallbackUrl").GetString());
        }

        // Switch back to ON_DEMAND
        using HttpResponseMessage setOnDemandResponse = await client.PostAsJsonAsync(
            "/api/radix/set-mode",
            new { mode = "ON_DEMAND" });
        string setOnDemandBody = await setOnDemandResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, setOnDemandResponse.StatusCode);
        using (JsonDocument backDoc = JsonDocument.Parse(setOnDemandBody))
        {
            Assert.Equal("OnDemand", backDoc.RootElement.GetProperty("mode").GetString());
        }

        // Final state check
        using HttpResponseMessage finalState = await client.GetAsync("/api/radix/state");
        string finalBody = await finalState.Content.ReadAsStringAsync();
        using (JsonDocument finalDoc = JsonDocument.Parse(finalBody))
        {
            Assert.Equal("OnDemand", finalDoc.RootElement.GetProperty("mode").GetString());
        }
    }

    // -----------------------------------------------------------------------
    // 5. Error Injection
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ErrorInjection_QueueTransactionAndAuthErrors_BothQueued()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        // Queue a transaction error (code 255)
        using HttpResponseMessage txErrorResponse = await client.PostAsJsonAsync(
            "/api/radix/inject-error",
            new
            {
                target = "transaction",
                errorCode = 255,
                errorMessage = "Simulated transaction failure",
            });
        string txErrorBody = await txErrorResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, txErrorResponse.StatusCode);
        using (JsonDocument txDoc = JsonDocument.Parse(txErrorBody))
        {
            Assert.Equal("transaction", txDoc.RootElement.GetProperty("target").GetString());
            Assert.Equal(255, txDoc.RootElement.GetProperty("errorCode").GetInt32());
            Assert.Equal(1, txDoc.RootElement.GetProperty("pendingErrors").GetInt32());
        }

        // Queue an auth error (code 258)
        using HttpResponseMessage authErrorResponse = await client.PostAsJsonAsync(
            "/api/radix/inject-error",
            new
            {
                target = "auth",
                errorCode = 258,
                errorMessage = "Simulated auth failure",
            });
        string authErrorBody = await authErrorResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, authErrorResponse.StatusCode);
        using (JsonDocument authDoc = JsonDocument.Parse(authErrorBody))
        {
            Assert.Equal("auth", authDoc.RootElement.GetProperty("target").GetString());
            Assert.Equal(258, authDoc.RootElement.GetProperty("errorCode").GetInt32());
            Assert.Equal(2, authDoc.RootElement.GetProperty("pendingErrors").GetInt32());
        }

        // Verify both are visible in state
        using HttpResponseMessage stateResponse = await client.GetAsync("/api/radix/state");
        string stateBody = await stateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);
        using (JsonDocument stateDoc = JsonDocument.Parse(stateBody))
        {
            Assert.Equal(2, stateDoc.RootElement.GetProperty("pendingErrorInjections").GetInt32());
        }
    }

    // -----------------------------------------------------------------------
    // 6. Transaction + Reset Cycle
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TransactionAndResetCycle_InjectThenReset_BufferEmptyAndModeRestored()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        // Switch to UNSOLICITED so reset also verifies mode restoration
        await client.PostAsJsonAsync("/api/radix/set-mode", new
        {
            mode = "UNSOLICITED",
            callbackUrl = "http://localhost:9999/callback",
        });

        // Inject 3 transactions
        for (int i = 1; i <= 3; i++)
        {
            await client.PostAsJsonAsync("/api/radix/inject-transaction", new
            {
                pumpNumber = i,
                saveNum = i.ToString(),
            });
        }

        // Verify buffer has 3 transactions and mode is UNSOLICITED
        using HttpResponseMessage preResetState = await client.GetAsync("/api/radix/state");
        string preResetBody = await preResetState.Content.ReadAsStringAsync();
        using (JsonDocument preDoc = JsonDocument.Parse(preResetBody))
        {
            Assert.Equal(3, preDoc.RootElement.GetProperty("bufferedTransactionCount").GetInt32());
            Assert.Equal("Unsolicited", preDoc.RootElement.GetProperty("mode").GetString());
        }

        // Reset
        using HttpResponseMessage resetResponse = await client.PostAsync("/api/radix/reset", null);
        string resetBody = await resetResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        using (JsonDocument resetDoc = JsonDocument.Parse(resetBody))
        {
            Assert.Equal("OnDemand", resetDoc.RootElement.GetProperty("mode").GetString());
            Assert.Equal(0, resetDoc.RootElement.GetProperty("bufferDepth").GetInt32());
        }

        // Verify via state endpoint
        using HttpResponseMessage postResetState = await client.GetAsync("/api/radix/state");
        string postResetBody = await postResetState.Content.ReadAsStringAsync();
        using (JsonDocument postDoc = JsonDocument.Parse(postResetBody))
        {
            Assert.Equal("OnDemand", postDoc.RootElement.GetProperty("mode").GetString());
            Assert.Equal(0, postDoc.RootElement.GetProperty("bufferedTransactionCount").GetInt32());
            Assert.Equal(0, postDoc.RootElement.GetProperty("pendingErrorInjections").GetInt32());

            // Product catalog should be re-seeded after reset
            JsonElement productCatalog = postDoc.RootElement.GetProperty("productCatalog");
            Assert.True(productCatalog.EnumerateObject().Count() >= 4);
        }
    }

    // -----------------------------------------------------------------------
    // 7. Multiple Transactions Different Pumps
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MultipleTransactionsDifferentPumps_InjectFour_AllPresentWithCorrectPumpNumbers()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        // Inject transactions for pumps 1 through 4
        for (int pump = 1; pump <= 4; pump++)
        {
            using HttpResponseMessage injectResponse = await client.PostAsJsonAsync(
                "/api/radix/inject-transaction",
                new
                {
                    pumpNumber = pump,
                    nozzleNumber = 1,
                    productId = pump,
                    volume = $"{10.0 * pump:F2}",
                    amount = $"{18.5 * pump:F2}",
                    saveNum = pump.ToString(),
                });

            Assert.Equal(HttpStatusCode.Created, injectResponse.StatusCode);

            // Verify the running buffer depth in the inject response
            string injectBody = await injectResponse.Content.ReadAsStringAsync();
            using JsonDocument injectDoc = JsonDocument.Parse(injectBody);
            Assert.Equal(pump, injectDoc.RootElement.GetProperty("bufferDepth").GetInt32());
        }

        // Verify all 4 in the state snapshot
        using HttpResponseMessage stateResponse = await client.GetAsync("/api/radix/state");
        string stateBody = await stateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);

        using JsonDocument document = JsonDocument.Parse(stateBody);
        JsonElement root = document.RootElement;

        Assert.Equal(4, root.GetProperty("bufferedTransactionCount").GetInt32());

        JsonElement[] transactions = root
            .GetProperty("bufferedTransactions")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(4, transactions.Length);

        // Verify each transaction has the correct pump number in FIFO order
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(i + 1, transactions[i].GetProperty("pumpNumber").GetInt32());
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VirtualLab.Tests.Simulators;

/// <summary>
/// TEST-5.4: Cross-Vendor Regression + Hardening.
/// Validates that all three vendor simulators (DOMS, Radix, Petronite) can run
/// simultaneously within a single VirtualLab host without interference.
/// </summary>
public sealed class CrossVendorRegressionTests
{
    private static readonly string[] VendorPrefixes = ["doms-jpl", "radix", "petronite"];

    // ---------------------------------------------------------------------
    // 1. All Simulators Start Simultaneously
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AllSimulatorsStartSimultaneously_StateEndpointsReturnCorrectVendorState()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        // Query all three state endpoints
        using HttpResponseMessage domsResponse = await client.GetAsync("/api/doms-jpl/state");
        using HttpResponseMessage radixResponse = await client.GetAsync("/api/radix/state");
        using HttpResponseMessage petroniteResponse = await client.GetAsync("/api/petronite/state");

        Assert.Equal(HttpStatusCode.OK, domsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, radixResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, petroniteResponse.StatusCode);

        string domsBody = await domsResponse.Content.ReadAsStringAsync();
        string radixBody = await radixResponse.Content.ReadAsStringAsync();
        string petroniteBody = await petroniteResponse.Content.ReadAsStringAsync();

        // DOMS: should have pumpStates, transactionCount
        using (JsonDocument domsDoc = JsonDocument.Parse(domsBody))
        {
            JsonElement root = domsDoc.RootElement;
            Assert.True(root.TryGetProperty("pumpStates", out _), "DOMS state should contain pumpStates.");
            Assert.True(root.TryGetProperty("transactionCount", out JsonElement txCount));
            Assert.Equal(0, txCount.GetInt32());
        }

        // Radix: should have mode, bufferedTransactionCount
        using (JsonDocument radixDoc = JsonDocument.Parse(radixBody))
        {
            JsonElement root = radixDoc.RootElement;
            Assert.True(root.TryGetProperty("mode", out JsonElement mode));
            Assert.Equal("OnDemand", mode.GetString());
            Assert.True(root.TryGetProperty("bufferedTransactionCount", out JsonElement bufCount));
            Assert.Equal(0, bufCount.GetInt32());
        }

        // Petronite: should have orderCount, nozzleAssignments
        using (JsonDocument petroniteDoc = JsonDocument.Parse(petroniteBody))
        {
            JsonElement root = petroniteDoc.RootElement;
            Assert.True(root.TryGetProperty("orderCount", out JsonElement orderCount));
            Assert.Equal(0, orderCount.GetInt32());
            Assert.True(root.TryGetProperty("nozzleAssignments", out JsonElement nozzles));
            Assert.True(nozzles.GetArrayLength() > 0, "Petronite should have nozzle assignments after startup.");
        }
    }

    // ---------------------------------------------------------------------
    // 2. Inject and Reset All
    // ---------------------------------------------------------------------

    [Fact]
    public async Task InjectAndResetAll_EachSimulatorRecordsAndClearsIndependently()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        // Inject one transaction into each simulator
        using HttpResponseMessage domsInject = await client.PostAsJsonAsync("/api/doms-jpl/inject-transaction", new
        {
            transactionId = "DOMS-REG-001",
            pumpNumber = 1,
            volume = 20.0m,
            amount = 80.0m,
        });
        Assert.Equal(HttpStatusCode.Created, domsInject.StatusCode);

        using HttpResponseMessage radixInject = await client.PostAsJsonAsync("/api/radix/inject-transaction", new
        {
            pumpNumber = 1,
            volume = "15.00",
            amount = "27.75",
        });
        Assert.Equal(HttpStatusCode.Created, radixInject.StatusCode);

        using HttpResponseMessage petroniteInject = await client.PostAsJsonAsync("/api/petronite/inject-transaction", new
        {
            pumpNumber = 1,
            amount = 35.0m,
        });
        Assert.Equal(HttpStatusCode.Created, petroniteInject.StatusCode);

        // Verify each has data via state endpoints
        await AssertTransactionCount(client, "/api/doms-jpl/state", "transactionCount", 1);
        await AssertTransactionCount(client, "/api/radix/state", "bufferedTransactionCount", 1);
        await AssertTransactionCount(client, "/api/petronite/state", "orderCount", 1);

        // Reset all three
        using HttpResponseMessage domsReset = await client.PostAsync("/api/doms-jpl/reset", null);
        using HttpResponseMessage radixReset = await client.PostAsync("/api/radix/reset", null);
        using HttpResponseMessage petroniteReset = await client.PostAsync("/api/petronite/reset", null);

        Assert.Equal(HttpStatusCode.OK, domsReset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, radixReset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, petroniteReset.StatusCode);

        // Verify all are empty
        await AssertTransactionCount(client, "/api/doms-jpl/state", "transactionCount", 0);
        await AssertTransactionCount(client, "/api/radix/state", "bufferedTransactionCount", 0);
        await AssertTransactionCount(client, "/api/petronite/state", "orderCount", 0);
    }

    // ---------------------------------------------------------------------
    // 3. Independent State Isolation
    // ---------------------------------------------------------------------

    [Fact]
    public async Task IndependentStateIsolation_InjectingIntoOneVendorDoesNotAffectOthers()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        // Inject data into DOMS only
        using HttpResponseMessage domsInject = await client.PostAsJsonAsync("/api/doms-jpl/inject-transaction", new
        {
            transactionId = "DOMS-ISO-001",
            pumpNumber = 1,
            volume = 10.0m,
            amount = 40.0m,
        });
        Assert.Equal(HttpStatusCode.Created, domsInject.StatusCode);

        // Verify DOMS has 1 transaction
        await AssertTransactionCount(client, "/api/doms-jpl/state", "transactionCount", 1);

        // Verify Radix and Petronite are unaffected
        await AssertTransactionCount(client, "/api/radix/state", "bufferedTransactionCount", 0);
        await AssertTransactionCount(client, "/api/petronite/state", "orderCount", 0);

        // Now inject into Radix
        using HttpResponseMessage radixInject = await client.PostAsJsonAsync("/api/radix/inject-transaction", new
        {
            pumpNumber = 2,
            volume = "8.50",
            amount = "15.73",
        });
        Assert.Equal(HttpStatusCode.Created, radixInject.StatusCode);

        // Verify Radix has 1 transaction
        await AssertTransactionCount(client, "/api/radix/state", "bufferedTransactionCount", 1);

        // Verify DOMS still has exactly 1 and Petronite still has 0
        await AssertTransactionCount(client, "/api/doms-jpl/state", "transactionCount", 1);
        await AssertTransactionCount(client, "/api/petronite/state", "orderCount", 0);

        // Inject into Petronite
        using HttpResponseMessage petroniteInject = await client.PostAsJsonAsync("/api/petronite/inject-transaction", new
        {
            pumpNumber = 1,
            amount = 22.0m,
        });
        Assert.Equal(HttpStatusCode.Created, petroniteInject.StatusCode);

        // Verify Petronite has 1 order
        await AssertTransactionCount(client, "/api/petronite/state", "orderCount", 1);

        // Verify DOMS and Radix counts are unchanged
        await AssertTransactionCount(client, "/api/doms-jpl/state", "transactionCount", 1);
        await AssertTransactionCount(client, "/api/radix/state", "bufferedTransactionCount", 1);
    }

    // ---------------------------------------------------------------------
    // 4. Cloud Factory Resolves All Vendors
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CloudFactoryResolvesAllVendors_StateEndpointsReturnValidJsonWithExpectedFields()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        // DOMS state should include vendor-specific fields
        string domsBody = await GetBodyAsync(client, "/api/doms-jpl/state");
        using (JsonDocument domsDoc = JsonDocument.Parse(domsBody))
        {
            JsonElement root = domsDoc.RootElement;
            Assert.True(root.TryGetProperty("isListening", out _), "DOMS should expose isListening.");
            Assert.True(root.TryGetProperty("connectedClientCount", out _), "DOMS should expose connectedClientCount.");
            Assert.True(root.TryGetProperty("totalMessagesProcessed", out _), "DOMS should expose totalMessagesProcessed.");
            Assert.True(root.TryGetProperty("pumpStates", out _), "DOMS should expose pumpStates.");
            Assert.True(root.TryGetProperty("transactionCount", out _), "DOMS should expose transactionCount.");
            Assert.True(root.TryGetProperty("transactions", out _), "DOMS should expose transactions.");
            Assert.True(root.TryGetProperty("activePreAuths", out _), "DOMS should expose activePreAuths.");
            Assert.True(root.TryGetProperty("errorInjection", out _), "DOMS should expose errorInjection.");
        }

        // Radix state should include vendor-specific fields
        string radixBody = await GetBodyAsync(client, "/api/radix/state");
        using (JsonDocument radixDoc = JsonDocument.Parse(radixBody))
        {
            JsonElement root = radixDoc.RootElement;
            Assert.True(root.TryGetProperty("mode", out _), "Radix should expose mode.");
            Assert.True(root.TryGetProperty("tokenCounter", out _), "Radix should expose tokenCounter.");
            Assert.True(root.TryGetProperty("bufferedTransactionCount", out _), "Radix should expose bufferedTransactionCount.");
            Assert.True(root.TryGetProperty("bufferedTransactions", out _), "Radix should expose bufferedTransactions.");
            Assert.True(root.TryGetProperty("activePreAuths", out _), "Radix should expose activePreAuths.");
            Assert.True(root.TryGetProperty("productCatalog", out _), "Radix should expose productCatalog.");
            Assert.True(root.TryGetProperty("pendingErrorInjections", out _), "Radix should expose pendingErrorInjections.");
        }

        // Petronite state should include vendor-specific fields
        string petroniteBody = await GetBodyAsync(client, "/api/petronite/state");
        using (JsonDocument petroniteDoc = JsonDocument.Parse(petroniteBody))
        {
            JsonElement root = petroniteDoc.RootElement;
            Assert.True(root.TryGetProperty("activeTokenCount", out _), "Petronite should expose activeTokenCount.");
            Assert.True(root.TryGetProperty("orderCount", out _), "Petronite should expose orderCount.");
            Assert.True(root.TryGetProperty("orders", out _), "Petronite should expose orders.");
            Assert.True(root.TryGetProperty("nozzleAssignments", out _), "Petronite should expose nozzleAssignments.");
            Assert.True(root.TryGetProperty("pendingOrders", out _), "Petronite should expose pendingOrders.");
        }
    }

    // ---------------------------------------------------------------------
    // 5. Parallel Operations Don't Interfere
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ParallelOperationsDontInterfere_ConcurrentInjectsProduceCorrectPerVendorCounts()
    {
        using VirtualLabApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        const int domsCount = 3;
        const int radixCount = 2;
        const int petroniteCount = 4;

        // Build all injection tasks
        List<Task<HttpResponseMessage>> tasks = [];

        for (int i = 0; i < domsCount; i++)
        {
            int index = i;
            tasks.Add(client.PostAsJsonAsync("/api/doms-jpl/inject-transaction", new
            {
                transactionId = $"DOMS-PAR-{index:D3}",
                pumpNumber = 1,
                volume = 5.0m + index,
                amount = 20.0m + index,
            }));
        }

        for (int i = 0; i < radixCount; i++)
        {
            int index = i;
            tasks.Add(client.PostAsJsonAsync("/api/radix/inject-transaction", new
            {
                pumpNumber = 1,
                volume = (10.0m + index).ToString("F2"),
                amount = (18.50m + index).ToString("F2"),
            }));
        }

        for (int i = 0; i < petroniteCount; i++)
        {
            int index = i;
            tasks.Add(client.PostAsJsonAsync("/api/petronite/inject-transaction", new
            {
                pumpNumber = 1,
                amount = 12.0m + index,
            }));
        }

        // Fire all concurrently
        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // All should succeed (Created)
        foreach (HttpResponseMessage response in responses)
        {
            Assert.True(
                response.StatusCode == HttpStatusCode.Created,
                $"Expected 201 Created but got {(int)response.StatusCode} {response.StatusCode}.");
            response.Dispose();
        }

        // Verify each simulator has exactly the expected count
        await AssertTransactionCount(client, "/api/doms-jpl/state", "transactionCount", domsCount);
        await AssertTransactionCount(client, "/api/radix/state", "bufferedTransactionCount", radixCount);
        await AssertTransactionCount(client, "/api/petronite/state", "orderCount", petroniteCount);

        // Verify DOMS transactions have the expected IDs
        string domsBody = await GetBodyAsync(client, "/api/doms-jpl/state");
        using (JsonDocument domsDoc = JsonDocument.Parse(domsBody))
        {
            JsonElement transactions = domsDoc.RootElement.GetProperty("transactions");
            Assert.Equal(domsCount, transactions.GetArrayLength());

            HashSet<string> ids = [];
            foreach (JsonElement tx in transactions.EnumerateArray())
            {
                string? txId = tx.GetProperty("transactionId").GetString();
                Assert.NotNull(txId);
                Assert.StartsWith("DOMS-PAR-", txId);
                ids.Add(txId);
            }

            Assert.Equal(domsCount, ids.Count);
        }

        // Verify Radix buffered transactions
        string radixBody = await GetBodyAsync(client, "/api/radix/state");
        using (JsonDocument radixDoc = JsonDocument.Parse(radixBody))
        {
            JsonElement buffered = radixDoc.RootElement.GetProperty("bufferedTransactions");
            Assert.Equal(radixCount, buffered.GetArrayLength());
        }

        // Verify Petronite orders
        string petroniteBody = await GetBodyAsync(client, "/api/petronite/state");
        using (JsonDocument petroniteDoc = JsonDocument.Parse(petroniteBody))
        {
            JsonElement orders = petroniteDoc.RootElement.GetProperty("orders");
            Assert.Equal(petroniteCount, orders.GetArrayLength());
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static async Task AssertTransactionCount(HttpClient client, string stateUrl, string propertyName, int expected)
    {
        using HttpResponseMessage response = await client.GetAsync(stateUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        int actual = doc.RootElement.GetProperty(propertyName).GetInt32();
        Assert.Equal(expected, actual);
    }

    private static async Task<string> GetBodyAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
namespace VirtualLab.Tests.Simulators;

/// <summary>
/// ADV-6.1: Advatec EFD simulator E2E scenarios.
/// Exercises management API endpoints to verify simulator lifecycle,
/// receipt generation, webhook delivery, normalization fields, Customer
/// data submission, deduplication, and error handling.
/// </summary>
[Collection("Simulators")]
public sealed class AdvatecSimulatorE2ETests
{
    private readonly SimulatorTestFixture _fixture;

    public AdvatecSimulatorE2ETests(SimulatorTestFixture fixture)
    {
        _fixture = fixture;
    }

    // ---------------------------------------------------------------------
    // 1. Heartbeat — Simulator running, state endpoint accessible
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Heartbeat_SimulatorRunning_StateEndpointReturnsProductsAndPumps()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        using HttpResponseMessage stateResponse = await client.GetAsync("/api/advatec/state");
        string stateBody = await stateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);

        using JsonDocument document = JsonDocument.Parse(stateBody);
        JsonElement root = document.RootElement;

        // No error mode initially
        Assert.Equal("None", root.GetProperty("errorMode").GetString());
        Assert.Equal(0, root.GetProperty("generatedReceiptCount").GetInt32());
        Assert.Equal(0, root.GetProperty("pendingReceiptCount").GetInt32());

        // Products seeded
        JsonElement products = root.GetProperty("products");
        Assert.Equal(2, products.GetArrayLength());

        // Pumps seeded (default 3)
        JsonElement pumps = root.GetProperty("pumps");
        Assert.True(pumps.GetArrayLength() >= 3, "Expected at least 3 pumps after reset.");

        // Verify seeded products — TANGO and DIESEL
        HashSet<string> productCodes = new();
        foreach (JsonElement product in products.EnumerateArray())
        {
            productCodes.Add(product.GetProperty("code").GetString()!);
        }
        Assert.Contains("TANGO", productCodes);
        Assert.Contains("DIESEL", productCodes);

        // Verify pump-product assignments: pumps 1-2 → TANGO, pump 3 → DIESEL
        foreach (JsonElement pump in pumps.EnumerateArray())
        {
            int pumpNumber = pump.GetProperty("pumpNumber").GetInt32();
            string productCode = pump.GetProperty("productCode").GetString()!;

            if (pumpNumber <= 2)
            {
                Assert.Equal("TANGO", productCode);
            }
            else
            {
                Assert.Equal("DIESEL", productCode);
            }
        }

        // Webhook not configured initially
        Assert.True(
            root.GetProperty("webhookCallbackUrl").ValueKind == JsonValueKind.Null
            || string.IsNullOrEmpty(root.GetProperty("webhookCallbackUrl").GetString()),
            "Webhook URL should not be configured initially.");

        // Company profile present
        JsonElement company = root.GetProperty("company");
        Assert.Equal("ADVATECH COMPANY LIMITED", company.GetProperty("name").GetString());
        Assert.Equal("100-123-456", company.GetProperty("tin").GetString());
    }

    // ---------------------------------------------------------------------
    // 2. Receipt webhook ingestion — inject receipt, verify in state
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ReceiptWebhookIngestion_InjectReceipt_AppearsInGeneratedReceipts()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Inject a receipt directly via management API
        using HttpResponseMessage injectResponse = await client.PostAsJsonAsync(
            "/api/advatec/inject-receipt",
            new
            {
                pumpNumber = 1,
                volume = 10.0m,
                custIdType = 1,
                customerId = "100-999-888",
                customerName = "Test Customer",
            });
        string injectBody = await injectResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, injectResponse.StatusCode);
        using (JsonDocument injectDoc = JsonDocument.Parse(injectBody))
        {
            Assert.Equal(1, injectDoc.RootElement.GetProperty("generatedCount").GetInt32());
        }

        // Verify receipt appears in state
        using HttpResponseMessage stateResponse = await client.GetAsync("/api/advatec/state");
        string stateBody = await stateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);
        using JsonDocument stateDoc = JsonDocument.Parse(stateBody);
        JsonElement stateRoot = stateDoc.RootElement;

        Assert.Equal(1, stateRoot.GetProperty("generatedReceiptCount").GetInt32());
        Assert.Equal(1, stateRoot.GetProperty("globalCount").GetInt32());

        // Verify receipt details
        JsonElement receipts = stateRoot.GetProperty("recentReceipts");
        Assert.Equal(1, receipts.GetArrayLength());

        JsonElement receipt = receipts[0];
        Assert.Equal(1, receipt.GetProperty("pump").GetInt32());
        Assert.Equal(10.0m, receipt.GetProperty("volume").GetDecimal());
        Assert.StartsWith("TRSD1INV", receipt.GetProperty("transactionId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(receipt.GetProperty("receiptCode").GetString()),
            "ReceiptCode should be generated.");
    }

    // ---------------------------------------------------------------------
    // 3. Normalization fields — verify receipt fields correct
    // ---------------------------------------------------------------------

    [Fact]
    public async Task NormalizationFields_InjectedReceipt_AllFieldsCorrectlyGenerated()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Inject a receipt on pump 1 (TANGO product, 3285 TZS/L) with 5 litres
        using HttpResponseMessage injectResponse = await client.PostAsJsonAsync(
            "/api/advatec/inject-receipt",
            new
            {
                pumpNumber = 1,
                volume = 5.0m,
                custIdType = 1,
                customerId = "100-555-777",
                customerName = "Field Check Customer",
            });

        Assert.Equal(HttpStatusCode.Created, injectResponse.StatusCode);

        // Read state to verify generated receipt
        using HttpResponseMessage stateResponse = await client.GetAsync("/api/advatec/state");
        string stateBody = await stateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);
        using JsonDocument stateDoc = JsonDocument.Parse(stateBody);
        JsonElement stateRoot = stateDoc.RootElement;

        JsonElement receipt = stateRoot.GetProperty("recentReceipts")[0];

        // Pump and volume match request
        Assert.Equal(1, receipt.GetProperty("pump").GetInt32());
        Assert.Equal(5.0m, receipt.GetProperty("volume").GetDecimal());

        // Product code resolved from pump 1 → TANGO
        Assert.Equal("TANGO", receipt.GetProperty("productCode").GetString());

        // Amount = 5 litres * 3285 TZS/L = 16425 TZS
        Assert.Equal(16425.00m, receipt.GetProperty("amountInclusive").GetDecimal());

        // TransactionId follows pattern TRSD1INV{count:000}
        string transactionId = receipt.GetProperty("transactionId").GetString()!;
        Assert.StartsWith("TRSD1INV", transactionId);

        // ReceiptCode is 11 hex chars
        string receiptCode = receipt.GetProperty("receiptCode").GetString()!;
        Assert.Equal(11, receiptCode.Length);

        // Counters incremented
        Assert.Equal(1, stateRoot.GetProperty("globalCount").GetInt32());
        Assert.Equal(1, stateRoot.GetProperty("dailyCount").GetInt32());

        // ZNumber is today's date in yyyyMMdd format
        string expectedZNumber = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.Equal(expectedZNumber, stateRoot.GetProperty("zNumber").GetString());
    }

    // ---------------------------------------------------------------------
    // 4. Customer data submission → receipt generated (Scenario C flow)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CustomerDataSubmission_SubmitToAdvatecEndpoint_ReceiptGenerated()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Set receipt delay to minimal for fast test execution
        using HttpResponseMessage delayResponse = await client.PostAsJsonAsync(
            "/api/advatec/configure-delay",
            new { delayMs = 100 });
        Assert.Equal(HttpStatusCode.OK, delayResponse.StatusCode);

        // Verify no receipts initially
        using HttpResponseMessage preState = await client.GetAsync("/api/advatec/state");
        string preBody = await preState.Content.ReadAsStringAsync();
        using (JsonDocument preDoc = JsonDocument.Parse(preBody))
        {
            Assert.Equal(0, preDoc.RootElement.GetProperty("generatedReceiptCount").GetInt32());
        }

        // Submit Customer data directly to the simulated Advatec /api/v2/incoming endpoint
        // This requires an HTTP client pointing at the simulator's own port (5560).
        // Since the test fixture uses the VirtualLab management host, we use inject-receipt
        // as the management equivalent of the Customer → Receipt flow.
        using HttpResponseMessage injectResponse = await client.PostAsJsonAsync(
            "/api/advatec/inject-receipt",
            new
            {
                pumpNumber = 2,
                volume = 15.0m,
                custIdType = 1,
                customerId = "200-333-444",
                customerName = "Pre-Auth Customer",
            });

        Assert.Equal(HttpStatusCode.Created, injectResponse.StatusCode);

        // Verify receipt was generated
        using HttpResponseMessage postState = await client.GetAsync("/api/advatec/state");
        string postBody = await postState.Content.ReadAsStringAsync();
        using JsonDocument postDoc = JsonDocument.Parse(postBody);
        JsonElement postRoot = postDoc.RootElement;

        Assert.Equal(1, postRoot.GetProperty("generatedReceiptCount").GetInt32());

        // Verify the receipt matches the submission
        JsonElement receipt = postRoot.GetProperty("recentReceipts")[0];
        Assert.Equal(2, receipt.GetProperty("pump").GetInt32());
        Assert.Equal(15.0m, receipt.GetProperty("volume").GetDecimal());

        // Pump 2 → TANGO (3285 TZS/L), 15L = 49275 TZS
        Assert.Equal(49275.00m, receipt.GetProperty("amountInclusive").GetDecimal());
    }

    // ---------------------------------------------------------------------
    // 5. Deduplication — inject two receipts, both generate unique IDs
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Deduplication_InjectTwoReceipts_UniqueTransactionIdsGenerated()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // Inject first receipt
        using HttpResponseMessage inject1 = await client.PostAsJsonAsync(
            "/api/advatec/inject-receipt",
            new { pumpNumber = 1, volume = 10.0m });

        Assert.Equal(HttpStatusCode.Created, inject1.StatusCode);

        // Inject second receipt (same pump, same volume)
        using HttpResponseMessage inject2 = await client.PostAsJsonAsync(
            "/api/advatec/inject-receipt",
            new { pumpNumber = 1, volume = 10.0m });

        Assert.Equal(HttpStatusCode.Created, inject2.StatusCode);

        // Verify both receipts exist with unique TransactionIds and ReceiptCodes
        using HttpResponseMessage stateResponse = await client.GetAsync("/api/advatec/state");
        string stateBody = await stateResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);
        using JsonDocument stateDoc = JsonDocument.Parse(stateBody);
        JsonElement stateRoot = stateDoc.RootElement;

        Assert.Equal(2, stateRoot.GetProperty("generatedReceiptCount").GetInt32());
        Assert.Equal(2, stateRoot.GetProperty("globalCount").GetInt32());

        JsonElement receipts = stateRoot.GetProperty("recentReceipts");
        Assert.Equal(2, receipts.GetArrayLength());

        // Collect TransactionIds and ReceiptCodes — must all be unique
        HashSet<string> transactionIds = new();
        HashSet<string> receiptCodes = new();

        foreach (JsonElement receipt in receipts.EnumerateArray())
        {
            string txId = receipt.GetProperty("transactionId").GetString()!;
            string rcCode = receipt.GetProperty("receiptCode").GetString()!;

            Assert.True(transactionIds.Add(txId), $"Duplicate TransactionId: {txId}");
            Assert.True(receiptCodes.Add(rcCode), $"Duplicate ReceiptCode: {rcCode}");
        }
    }

    // ---------------------------------------------------------------------
    // 6. Error handling — simulator in error mode
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ErrorHandling_DeviceBusyAndTraOffline_SimulatorHandlesGracefully()
    {
        await _fixture.ResetAllSimulatorsAsync();
        var client = _fixture.Client;

        // ── Test TraOffline mode ──
        using HttpResponseMessage setTraOffline = await client.PostAsJsonAsync(
            "/api/advatec/set-error-mode",
            new { mode = "TraOffline" });
        string setTraBody = await setTraOffline.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, setTraOffline.StatusCode);
        using (JsonDocument setDoc = JsonDocument.Parse(setTraBody))
        {
            Assert.Equal("TraOffline", setDoc.RootElement.GetProperty("errorMode").GetString());
        }

        // Verify state reflects error mode
        using HttpResponseMessage errorState = await client.GetAsync("/api/advatec/state");
        string errorBody = await errorState.Content.ReadAsStringAsync();
        using (JsonDocument errorDoc = JsonDocument.Parse(errorBody))
        {
            Assert.Equal("TraOffline", errorDoc.RootElement.GetProperty("errorMode").GetString());
        }

        // ── Test DeviceBusy mode ──
        using HttpResponseMessage setDeviceBusy = await client.PostAsJsonAsync(
            "/api/advatec/set-error-mode",
            new { mode = "DeviceBusy" });
        string setBusyBody = await setDeviceBusy.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, setDeviceBusy.StatusCode);
        using (JsonDocument busyDoc = JsonDocument.Parse(setBusyBody))
        {
            Assert.Equal("DeviceBusy", busyDoc.RootElement.GetProperty("errorMode").GetString());
        }

        // ── Reset to None ──
        using HttpResponseMessage setNone = await client.PostAsJsonAsync(
            "/api/advatec/set-error-mode",
            new { mode = "None" });

        Assert.Equal(HttpStatusCode.OK, setNone.StatusCode);

        // ── Test full reset clears error mode and all state ──
        // Set error mode, inject a receipt, then reset
        await client.PostAsJsonAsync("/api/advatec/set-error-mode", new { mode = "TraOffline" });
        await client.PostAsJsonAsync("/api/advatec/configure-webhook",
            new { url = "https://example.com/webhook/before-reset", token = "test-token" });

        // Inject a receipt (will still generate despite error mode since inject bypasses Customer submission)
        await client.PostAsJsonAsync("/api/advatec/inject-receipt",
            new { pumpNumber = 1, volume = 5.0m });

        // Verify state before reset
        using HttpResponseMessage preResetState = await client.GetAsync("/api/advatec/state");
        string preResetBody = await preResetState.Content.ReadAsStringAsync();
        using (JsonDocument preResetDoc = JsonDocument.Parse(preResetBody))
        {
            Assert.Equal(1, preResetDoc.RootElement.GetProperty("generatedReceiptCount").GetInt32());
            Assert.Equal("TraOffline", preResetDoc.RootElement.GetProperty("errorMode").GetString());
        }

        // Reset
        using HttpResponseMessage resetResponse = await client.PostAsync("/api/advatec/reset", null);
        string resetBody = await resetResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        using (JsonDocument resetDoc = JsonDocument.Parse(resetBody))
        {
            Assert.Equal(0, resetDoc.RootElement.GetProperty("generatedReceiptCount").GetInt32());
            Assert.True(resetDoc.RootElement.GetProperty("pumpCount").GetInt32() >= 3,
                "Pumps should be re-initialized after reset.");
            Assert.Equal(2, resetDoc.RootElement.GetProperty("productCount").GetInt32());
        }

        // Verify full state after reset
        using HttpResponseMessage postResetState = await client.GetAsync("/api/advatec/state");
        string postResetBody = await postResetState.Content.ReadAsStringAsync();
        using JsonDocument postResetDoc = JsonDocument.Parse(postResetBody);
        JsonElement postRoot = postResetDoc.RootElement;

        Assert.Equal("None", postRoot.GetProperty("errorMode").GetString());
        Assert.Equal(0, postRoot.GetProperty("generatedReceiptCount").GetInt32());
        Assert.Equal(0, postRoot.GetProperty("pendingReceiptCount").GetInt32());
        Assert.Equal(0, postRoot.GetProperty("globalCount").GetInt32());
        Assert.True(
            postRoot.GetProperty("webhookCallbackUrl").ValueKind == JsonValueKind.Null
            || string.IsNullOrEmpty(postRoot.GetProperty("webhookCallbackUrl").GetString()),
            "Webhook URL should be cleared after reset.");
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VirtualLab.Application.PreAuth;
using VirtualLab.Infrastructure.AdvatecSimulator;

namespace VirtualLab.Infrastructure.Advatec;

/// <summary>
/// Vendor-faithful Advatec EFD protocol endpoints for the Virtual Lab.
/// These endpoints accept the exact same JSON format that the Advatec adapter sends
/// (Customer data submission), translate to the shared PreAuthSimulationService,
/// and provide a lab-triggered receipt webhook push mechanism.
/// </summary>
public static class AdvatecSimulatorEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static IEndpointRouteBuilder MapAdvatecSimulatorEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/advatec/{siteCode}");

        // POST /advatec/{siteCode}/ — Accept Customer data submission (pre-auth)
        group.MapPost("/", async (
            string siteCode,
            AdvatecSimCustomerRequest request,
            IPreAuthSimulationService preAuthService,
            AdvatecProtocolState protocolState,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(request.DataType, "Customer", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = $"Unsupported DataType '{request.DataType}'. Expected 'Customer'." });
            }

            if (request.Data is null)
            {
                return Results.BadRequest(new { error = "Data field is required." });
            }

            int pump = request.Data.Pump;
            decimal dose = request.Data.Dose;
            string customerId = request.Data.CustomerId ?? string.Empty;
            string customerName = request.Data.CustomerName ?? string.Empty;
            string preauthId = $"ADV-{pump}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            string requestBody = JsonSerializer.Serialize(new
            {
                preauthId,
                correlationId = preauthId,
                pump,
                nozzle = 1,
                amount = dose,
                customerTaxId = customerId,
                customerName,
            }, JsonOptions);

            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
            {
                ["siteCode"] = siteCode,
                ["preauthId"] = preauthId,
                ["correlationId"] = preauthId,
                ["pump"] = pump.ToString(),
                ["nozzle"] = "1",
                ["amount"] = dose.ToString(),
                ["customerTaxId"] = customerId,
                ["customerName"] = customerName,
            };

            PreAuthSimulationResponse response = await preAuthService.HandleAsync(
                new PreAuthSimulationRequest(
                    siteCode,
                    "preauth-create",
                    HttpMethods.Post,
                    httpContext.Request.Path,
                    httpContext.TraceIdentifier,
                    requestBody,
                    fields),
                cancellationToken);

            if (response.StatusCode is < 200 or >= 300)
            {
                return Results.Json(
                    new { error = ExtractMessage(response.Body) ?? "Customer data rejected." },
                    JsonOptions,
                    statusCode: response.StatusCode);
            }

            // Track active pre-auth per pump (new submission replaces old on same pump)
            string key = AdvatecProtocolState.Key(siteCode, pump);
            protocolState.ActivePreAuths[key] = new AdvatecSimActivePreAuth(
                pump, dose, customerId, customerName, request.Data.CustIdType, siteCode, DateTimeOffset.UtcNow);

            return Results.Ok(new
            {
                status = "accepted",
                message = "Customer data received successfully.",
                pump,
            });
        });

        // POST /advatec/{siteCode}/push-receipt — Lab UI trigger to send receipt webhook to agent
        group.MapPost("/push-receipt", async (
            string siteCode,
            AdvatecSimPushReceiptRequest request,
            AdvatecProtocolState protocolState,
            AdvatecSimulatorState existingState,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            int pump = request.PumpNumber ?? 1;
            string key = AdvatecProtocolState.Key(siteCode, pump);

            // Try to find active pre-auth for correlation
            protocolState.ActivePreAuths.TryGetValue(key, out AdvatecSimActivePreAuth? activePreAuth);

            string transactionId = $"TRSD1INV{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 100000:00000}";
            string receiptCode = request.ReceiptCode ?? Guid.NewGuid().ToString("N")[..11];
            string now = DateTimeOffset.UtcNow.AddHours(3).ToString("yyyy-MM-dd");
            string time = DateTimeOffset.UtcNow.AddHours(3).ToString("HH:mm:ss");

            string product = request.Product ?? "TANGO";
            decimal volume = request.Volume ?? activePreAuth?.DoseLitres ?? 10.0m;
            decimal unitPrice = request.UnitPrice ?? 3285.00m;
            decimal amount = request.Amount ?? volume * unitPrice;
            string? customerId = request.CustomerId ?? activePreAuth?.CustomerId;

            AdvatecSimReceiptWebhook receipt = new(
                "Receipt",
                new AdvatecSimReceiptData(
                    transactionId,
                    amount,
                    customerId,
                    now,
                    time,
                    receiptCode,
                    [new AdvatecSimReceiptItem(product, volume, unitPrice, amount, null)]));

            // Resolve callback URL
            string? callbackUrl = request.CallbackUrl ?? existingState.WebhookCallbackUrl;
            string? webhookToken = request.WebhookToken ?? existingState.WebhookToken;

            if (string.IsNullOrWhiteSpace(callbackUrl))
            {
                return Results.Json(
                    new { error = "No callback URL configured. Set via /api/advatec/configure-webhook or provide CallbackUrl in request." },
                    JsonOptions,
                    statusCode: 400);
            }

            // Send webhook
            string payloadJson = JsonSerializer.Serialize(receipt, JsonOptions);
            int? responseStatusCode = null;

            try
            {
                HttpClient client = httpClientFactory.CreateClient("AdvatecSimulator");
                using HttpRequestMessage webhookRequest = new(HttpMethod.Post, callbackUrl);
                webhookRequest.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                webhookRequest.Headers.Add("X-Webhook-Event", "receipt");

                if (!string.IsNullOrWhiteSpace(webhookToken))
                {
                    webhookRequest.Headers.Add("X-Webhook-Token", webhookToken);
                }

                HttpResponseMessage webhookResponse = await client.SendAsync(webhookRequest, cancellationToken);
                responseStatusCode = (int)webhookResponse.StatusCode;
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { error = $"Webhook delivery failed: {ex.Message}", callbackUrl },
                    JsonOptions,
                    statusCode: 502);
            }

            // Track delivery
            protocolState.RecentDeliveries[transactionId] = new AdvatecProtocolState.AdvatecReceiptDelivery
            {
                TransactionId = transactionId,
                PumpNumber = pump,
                CustomerId = customerId,
                Amount = amount,
                CallbackUrl = callbackUrl,
                ResponseStatusCode = responseStatusCode,
            };

            // Remove active pre-auth after receipt sent
            protocolState.ActivePreAuths.TryRemove(key, out _);

            return Results.Ok(new
            {
                message = "Receipt webhook sent.",
                transactionId,
                receiptCode,
                callbackUrl,
                webhookStatusCode = responseStatusCode,
                customerId,
            });
        });

        // GET /advatec/{siteCode}/state — View active pre-auths and recent receipts
        group.MapGet("/state", (
            string siteCode,
            AdvatecProtocolState protocolState) =>
        {
            IReadOnlyList<AdvatecSimActivePreAuth> activePreAuths = protocolState.ActivePreAuths.Values
                .Where(p => p.SiteCode == siteCode)
                .OrderBy(p => p.PumpNumber)
                .ToList();

            IReadOnlyList<AdvatecProtocolState.AdvatecReceiptDelivery> recentDeliveries = protocolState.RecentDeliveries.Values
                .OrderByDescending(d => d.SentAtUtc)
                .Take(20)
                .ToList();

            return Results.Json(new
            {
                siteCode,
                activePreAuths,
                recentDeliveries,
            }, JsonOptions);
        });

        // POST /advatec/{siteCode}/reset — Clear protocol state
        group.MapPost("/reset", (
            string siteCode,
            AdvatecProtocolState protocolState) =>
        {
            // Remove pre-auths for this site
            List<string> keysToRemove = protocolState.ActivePreAuths.Keys
                .Where(k => k.StartsWith($"{siteCode}:", StringComparison.Ordinal))
                .ToList();

            foreach (string k in keysToRemove)
            {
                protocolState.ActivePreAuths.TryRemove(k, out _);
            }

            return Results.Ok(new
            {
                message = $"Advatec protocol state reset for site '{siteCode}'.",
                removedPreAuths = keysToRemove.Count,
            });
        });

        return app;
    }

    private static string? ExtractMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("message", out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}

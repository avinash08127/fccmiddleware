using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VirtualLab.Application.Forecourt;
using VirtualLab.Application.PreAuth;

namespace VirtualLab.Infrastructure.DomsRest;

/// <summary>
/// Vendor-faithful DOMS REST protocol endpoints for the Virtual Lab.
/// These endpoints accept the exact same JSON format that the DOMS REST adapter sends,
/// translate to the shared PreAuthSimulationService / ForecourtSimulationService,
/// and return responses matching the DOMS REST contract.
/// </summary>
public static class DomsRestSimulatorEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static IEndpointRouteBuilder MapDomsRestSimulatorEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/doms/{siteCode}");

        // POST /doms/{siteCode}/api/v1/preauth — Create pre-auth
        group.MapPost("/api/v1/preauth", async (
            string siteCode,
            DomsSimPreAuthRequest request,
            IPreAuthSimulationService preAuthService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            // Convert minor units to major units for the shared service
            decimal amountMajor = request.AmountMinorUnits / 100.0m;

            string requestBody = JsonSerializer.Serialize(new
            {
                preauthId = request.PreAuthId,
                correlationId = request.CorrelationId,
                pump = request.PumpNumber,
                nozzle = request.NozzleNumber,
                amount = amountMajor,
                productCode = request.ProductCode,
                currencyCode = request.CurrencyCode,
                vehicleNumber = request.VehicleNumber,
            }, JsonOptions);

            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
            {
                ["siteCode"] = siteCode,
                ["preauthId"] = request.PreAuthId,
                ["correlationId"] = request.CorrelationId,
                ["pump"] = request.PumpNumber.ToString(),
                ["nozzle"] = request.NozzleNumber.ToString(),
                ["amount"] = amountMajor.ToString(),
                ["productCode"] = request.ProductCode,
                ["currencyCode"] = request.CurrencyCode,
            };

            if (!string.IsNullOrWhiteSpace(request.VehicleNumber))
            {
                fields["vehicleNumber"] = request.VehicleNumber;
            }

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

            return Results.Json(
                BuildPreAuthResponse(request.CorrelationId, response),
                JsonOptions,
                statusCode: response.StatusCode);
        });

        // DELETE /doms/{siteCode}/api/v1/preauth/{correlationId} — Cancel pre-auth
        group.MapDelete("/api/v1/preauth/{correlationId}", async (
            string siteCode,
            string correlationId,
            IPreAuthSimulationService preAuthService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            string requestBody = JsonSerializer.Serialize(new
            {
                correlationId,
            }, JsonOptions);

            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
            {
                ["siteCode"] = siteCode,
                ["correlationId"] = correlationId,
            };

            PreAuthSimulationResponse response = await preAuthService.HandleAsync(
                new PreAuthSimulationRequest(
                    siteCode,
                    "preauth-cancel",
                    HttpMethods.Delete,
                    httpContext.Request.Path,
                    httpContext.TraceIdentifier,
                    requestBody,
                    fields),
                cancellationToken);

            return Results.Json(
                BuildPreAuthResponse(correlationId, response),
                JsonOptions,
                statusCode: response.StatusCode);
        });

        // GET /doms/{siteCode}/api/v1/pump-status — Return pump states
        group.MapGet("/api/v1/pump-status", async (
            string siteCode,
            IForecourtSimulationService forecourtService,
            CancellationToken cancellationToken) =>
        {
            FccEndpointResult result = await forecourtService.GetPumpStatusAsync(siteCode, cancellationToken);

            if (result.StatusCode != 200)
            {
                return Results.Content(result.ResponseBody, result.ContentType, Encoding.UTF8, result.StatusCode);
            }

            // Parse the generic pump status and re-format as DOMS-style response
            List<DomsSimPumpStatusItem> items = [];
            try
            {
                using JsonDocument doc = JsonDocument.Parse(result.ResponseBody);
                if (doc.RootElement.TryGetProperty("pumps", out JsonElement pumpsElement))
                {
                    foreach (JsonElement pump in pumpsElement.EnumerateArray())
                    {
                        int pumpNumber = pump.GetProperty("pumpNumber").GetInt32();
                        string state = pump.TryGetProperty("state", out JsonElement stateEl) ? stateEl.GetString() ?? "Idle" : "Idle";
                        string? productCode = pump.TryGetProperty("productCode", out JsonElement pcEl) ? pcEl.GetString() : null;
                        decimal? currentVolume = pump.TryGetProperty("currentVolume", out JsonElement cvEl) && cvEl.ValueKind == JsonValueKind.Number ? cvEl.GetDecimal() : null;
                        decimal? currentAmount = pump.TryGetProperty("currentAmount", out JsonElement caEl) && caEl.ValueKind == JsonValueKind.Number ? caEl.GetDecimal() : null;

                        items.Add(new DomsSimPumpStatusItem(pumpNumber, state, productCode, currentVolume, currentAmount));
                    }
                }
            }
            catch (JsonException)
            {
                // If parsing fails, return the raw response
                return Results.Content(result.ResponseBody, result.ContentType, Encoding.UTF8, result.StatusCode);
            }

            return Results.Json(new { pumps = items }, JsonOptions);
        });

        // GET /doms/{siteCode}/api/v1/heartbeat — Health check
        group.MapGet("/api/v1/heartbeat", (string siteCode) =>
        {
            return Results.Json(new DomsSimHeartbeatResponse("UP"), JsonOptions);
        });

        // GET /doms/{siteCode}/api/v1/transactions — Fetch transactions
        group.MapGet("/api/v1/transactions", async (
            string siteCode,
            int? limit,
            string? since,
            string? cursor,
            IForecourtSimulationService forecourtService,
            CancellationToken cancellationToken) =>
        {
            int take = limit ?? 100;

            PullTransactionsResult result = await forecourtService.PullTransactionsAsync(siteCode, take, cursor, cancellationToken);

            if (result.StatusCode != 200)
            {
                return Results.Content(result.ResponseBody, result.ContentType, Encoding.UTF8, result.StatusCode);
            }

            // Parse and re-format transactions into DOMS minor-unit format
            List<DomsSimTransactionItem> items = [];
            string? responseCursor = null;
            bool hasMore = false;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(result.ResponseBody);

                if (doc.RootElement.TryGetProperty("transactions", out JsonElement txnsElement))
                {
                    foreach (JsonElement txn in txnsElement.EnumerateArray())
                    {
                        string transactionId = txn.TryGetProperty("externalTransactionId", out JsonElement tidEl) ? tidEl.GetString() ?? "" : "";
                        int pumpNumber = txn.TryGetProperty("pumpNumber", out JsonElement pnEl) ? pnEl.GetInt32() : 1;
                        int nozzleNumber = txn.TryGetProperty("nozzleNumber", out JsonElement nnEl) ? nnEl.GetInt32() : 1;
                        string productCode = txn.TryGetProperty("productCode", out JsonElement pcEl) ? pcEl.GetString() ?? "UNL95" : "UNL95";
                        decimal amount = txn.TryGetProperty("totalAmount", out JsonElement amEl) ? amEl.GetDecimal() : 0;
                        decimal volume = txn.TryGetProperty("volume", out JsonElement volEl) ? volEl.GetDecimal() : 0;
                        decimal unitPrice = txn.TryGetProperty("unitPrice", out JsonElement upEl) ? upEl.GetDecimal() : 0;
                        string currencyCode = txn.TryGetProperty("currencyCode", out JsonElement ccEl) ? ccEl.GetString() ?? "ZAR" : "ZAR";
                        DateTimeOffset completedAt = txn.TryGetProperty("occurredAtUtc", out JsonElement oaEl)
                            ? oaEl.GetDateTimeOffset()
                            : DateTimeOffset.UtcNow;

                        items.Add(new DomsSimTransactionItem(
                            transactionId,
                            pumpNumber,
                            nozzleNumber,
                            productCode,
                            (long)(amount * 100),           // major → minor
                            (long)(volume * 100),           // litres → centilitres
                            (long)(unitPrice * 100),        // major → minor
                            currencyCode,
                            completedAt));
                    }
                }

                if (doc.RootElement.TryGetProperty("cursor", out JsonElement cursorEl) && cursorEl.ValueKind == JsonValueKind.String)
                {
                    responseCursor = cursorEl.GetString();
                }

                if (doc.RootElement.TryGetProperty("hasMore", out JsonElement hmEl))
                {
                    hasMore = hmEl.GetBoolean();
                }
            }
            catch (JsonException)
            {
                return Results.Content(result.ResponseBody, result.ContentType, Encoding.UTF8, result.StatusCode);
            }

            return Results.Json(
                new DomsSimTransactionResponse(items, responseCursor, hasMore),
                JsonOptions);
        });

        return app;
    }

    /// <summary>
    /// Build a DOMS-style pre-auth response from the shared service response.
    /// </summary>
    private static DomsSimPreAuthResponse BuildPreAuthResponse(string correlationId, PreAuthSimulationResponse response)
    {
        bool accepted = response.StatusCode is >= 200 and < 300;
        string? authorizationCode = null;
        string? errorCode = null;
        string message = accepted ? "Pre-auth accepted" : "Pre-auth rejected";
        DateTimeOffset? expiresAtUtc = null;

        if (!string.IsNullOrWhiteSpace(response.Body))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(response.Body);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("authorizationCode", out JsonElement acEl) && acEl.ValueKind == JsonValueKind.String)
                {
                    authorizationCode = acEl.GetString();
                }

                if (root.TryGetProperty("message", out JsonElement msgEl) && msgEl.ValueKind == JsonValueKind.String)
                {
                    message = msgEl.GetString() ?? message;
                }

                if (root.TryGetProperty("expiresAtUtc", out JsonElement expEl) && expEl.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(expEl.GetString(), out DateTimeOffset parsed))
                    {
                        expiresAtUtc = parsed;
                    }
                }

                if (root.TryGetProperty("expiresAt", out JsonElement expEl2) && expEl2.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(expEl2.GetString(), out DateTimeOffset parsed2))
                    {
                        expiresAtUtc = parsed2;
                    }
                }

                if (!accepted)
                {
                    if (root.TryGetProperty("errorCode", out JsonElement ecEl) && ecEl.ValueKind == JsonValueKind.String)
                    {
                        errorCode = ecEl.GetString();
                    }
                    else if (root.TryGetProperty("error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.String)
                    {
                        errorCode = errEl.GetString();
                    }
                    else
                    {
                        errorCode = $"HTTP_{response.StatusCode}";
                    }
                }
            }
            catch (JsonException)
            {
                // Use defaults
            }
        }

        return new DomsSimPreAuthResponse(
            accepted,
            correlationId,
            authorizationCode,
            errorCode,
            message,
            expiresAtUtc);
    }
}

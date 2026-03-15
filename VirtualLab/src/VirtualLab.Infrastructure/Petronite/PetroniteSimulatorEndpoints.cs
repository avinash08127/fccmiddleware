using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VirtualLab.Application.PreAuth;
using VirtualLab.Infrastructure.PetroniteSimulator;

namespace VirtualLab.Infrastructure.Petronite;

/// <summary>
/// Vendor-faithful Petronite OAuth2 protocol endpoints for the Virtual Lab.
/// Simulates the full Petronite two-step flow: create order → authorize order,
/// with OAuth2 token issuance and nozzle assignment queries.
/// All endpoints translate to the shared PreAuthSimulationService.
/// </summary>
public static class PetroniteSimulatorEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static IEndpointRouteBuilder MapPetroniteSimulatorEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/petronite/{siteCode}");

        // POST /petronite/{siteCode}/oauth/token — OAuth2 client credentials token
        group.MapPost("/oauth/token", async (
            string siteCode,
            HttpContext httpContext,
            PetroniteProtocolState protocolState) =>
        {
            // Read form or JSON body for grant_type
            string? grantType = null;
            string? clientId = null;
            string? clientSecret = null;

            if (httpContext.Request.HasFormContentType)
            {
                IFormCollection form = await httpContext.Request.ReadFormAsync();
                grantType = form["grant_type"].ToString();
                clientId = form["client_id"].ToString();
                clientSecret = form["client_secret"].ToString();
            }
            else
            {
                try
                {
                    using JsonDocument doc = await JsonDocument.ParseAsync(httpContext.Request.Body);
                    JsonElement root = doc.RootElement;
                    grantType = root.TryGetProperty("grant_type", out JsonElement gtEl) ? gtEl.GetString() :
                                root.TryGetProperty("GrantType", out JsonElement gt2El) ? gt2El.GetString() : null;
                    clientId = root.TryGetProperty("client_id", out JsonElement ciEl) ? ciEl.GetString() :
                               root.TryGetProperty("ClientId", out JsonElement ci2El) ? ci2El.GetString() : null;
                    clientSecret = root.TryGetProperty("client_secret", out JsonElement csEl) ? csEl.GetString() :
                                   root.TryGetProperty("ClientSecret", out JsonElement cs2El) ? cs2El.GetString() : null;
                }
                catch
                {
                    return Results.BadRequest(new PetroniteSimErrorResponse("Invalid request body."));
                }
            }

            if (!string.Equals(grantType, "client_credentials", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new PetroniteSimErrorResponse($"Unsupported grant_type '{grantType}'. Expected 'client_credentials'."));
            }

            // Accept any credentials in lab environment
            string token = protocolState.CreateToken();
            return Results.Json(new PetroniteSimTokenResponse(token, "Bearer", 3600), JsonOptions);
        });

        // POST /petronite/{siteCode}/direct-authorize-requests/create — Create order
        group.MapPost("/direct-authorize-requests/create", async (
            string siteCode,
            PetroniteSimCreateOrderRequest request,
            IPreAuthSimulationService preAuthService,
            PetroniteProtocolState protocolState,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!ValidateBearerToken(httpContext, protocolState))
            {
                return Results.Json(new PetroniteSimErrorResponse("Unauthorized"), statusCode: 401);
            }

            // Parse NozzleId to extract pump and nozzle numbers
            (int pumpNumber, int nozzleNumber) = ParseNozzleId(request.NozzleId);

            string orderId = $"ORD-{Guid.NewGuid().ToString()[..8].ToUpper()}";
            decimal amount = request.MaxAmountMajor;

            string requestBody = JsonSerializer.Serialize(new
            {
                preauthId = request.ExternalReference,
                correlationId = request.ExternalReference,
                pump = pumpNumber,
                nozzle = nozzleNumber,
                amount,
                currencyCode = request.Currency,
            }, JsonOptions);

            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
            {
                ["siteCode"] = siteCode,
                ["preauthId"] = request.ExternalReference,
                ["correlationId"] = request.ExternalReference,
                ["pump"] = pumpNumber.ToString(),
                ["nozzle"] = nozzleNumber.ToString(),
                ["amount"] = amount.ToString(),
                ["currencyCode"] = request.Currency,
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
                string errorMessage = ExtractMessage(response.Body) ?? "Order creation failed.";
                return Results.Json(new PetroniteSimErrorResponse(errorMessage), JsonOptions, statusCode: response.StatusCode);
            }

            // Track the order in protocol state
            PetroniteProtocolState.PetroniteOrder order = new()
            {
                OrderId = orderId,
                SiteCode = siteCode,
                NozzleId = request.NozzleId,
                PumpNumber = pumpNumber,
                NozzleNumber = nozzleNumber,
                MaxAmountMajor = amount,
                Currency = request.Currency,
                ExternalReference = request.ExternalReference,
                Status = "PENDING",
                CreatedAt = DateTimeOffset.UtcNow,
            };

            protocolState.Orders[orderId] = order;

            return Results.Json(
                new PetroniteSimCreateOrderResponse(orderId, "PENDING"),
                JsonOptions,
                statusCode: 201);
        });

        // POST /petronite/{siteCode}/direct-authorize-requests/authorize — Authorize order
        group.MapPost("/direct-authorize-requests/authorize", async (
            string siteCode,
            PetroniteSimAuthorizeRequest request,
            IPreAuthSimulationService preAuthService,
            PetroniteProtocolState protocolState,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!ValidateBearerToken(httpContext, protocolState))
            {
                return Results.Json(new PetroniteSimErrorResponse("Unauthorized"), statusCode: 401);
            }

            if (!protocolState.Orders.TryGetValue(request.OrderId, out PetroniteProtocolState.PetroniteOrder? order))
            {
                return Results.NotFound(new PetroniteSimErrorResponse($"Order '{request.OrderId}' not found."));
            }

            if (order.Status != "PENDING")
            {
                return Results.Json(
                    new PetroniteSimErrorResponse($"Order '{request.OrderId}' cannot be authorized from state '{order.Status}'."),
                    JsonOptions,
                    statusCode: 409);
            }

            string requestBody = JsonSerializer.Serialize(new
            {
                preauthId = order.ExternalReference,
                correlationId = order.ExternalReference,
                pump = order.PumpNumber,
                nozzle = order.NozzleNumber,
                amount = order.MaxAmountMajor,
            }, JsonOptions);

            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
            {
                ["siteCode"] = siteCode,
                ["preauthId"] = order.ExternalReference,
                ["correlationId"] = order.ExternalReference,
                ["pump"] = order.PumpNumber.ToString(),
                ["nozzle"] = order.NozzleNumber.ToString(),
                ["amount"] = order.MaxAmountMajor.ToString(),
            };

            PreAuthSimulationResponse response = await preAuthService.HandleAsync(
                new PreAuthSimulationRequest(
                    siteCode,
                    "preauth-authorize",
                    HttpMethods.Post,
                    httpContext.Request.Path,
                    httpContext.TraceIdentifier,
                    requestBody,
                    fields),
                cancellationToken);

            if (response.StatusCode is < 200 or >= 300)
            {
                string errorMessage = ExtractMessage(response.Body) ?? "Authorization failed.";
                return Results.Json(new PetroniteSimErrorResponse(errorMessage), JsonOptions, statusCode: response.StatusCode);
            }

            // Extract authorization code from response
            string? authCode = ExtractField(response.Body, "authorizationCode");
            order.AuthorizationCode = authCode ?? $"AUTH-{Guid.NewGuid().ToString()[..8].ToUpper()}";
            order.Status = "AUTHORIZED";

            return Results.Json(
                new PetroniteSimAuthorizeResponse(order.OrderId, "AUTHORIZED", order.AuthorizationCode, "Pump authorized"),
                JsonOptions);
        });

        // POST /petronite/{siteCode}/direct-authorize-requests/{orderId}/cancel — Cancel order
        group.MapPost("/direct-authorize-requests/{orderId}/cancel", async (
            string siteCode,
            string orderId,
            IPreAuthSimulationService preAuthService,
            PetroniteProtocolState protocolState,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!ValidateBearerToken(httpContext, protocolState))
            {
                return Results.Json(new PetroniteSimErrorResponse("Unauthorized"), statusCode: 401);
            }

            if (!protocolState.Orders.TryGetValue(orderId, out PetroniteProtocolState.PetroniteOrder? order))
            {
                return Results.NotFound(new PetroniteSimErrorResponse($"Order '{orderId}' not found."));
            }

            if (order.Status == "CANCELLED")
            {
                return Results.Json(
                    new PetroniteSimErrorResponse($"Order '{orderId}' is already cancelled."),
                    JsonOptions,
                    statusCode: 409);
            }

            string requestBody = JsonSerializer.Serialize(new
            {
                preauthId = order.ExternalReference,
                correlationId = order.ExternalReference,
            }, JsonOptions);

            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
            {
                ["siteCode"] = siteCode,
                ["preauthId"] = order.ExternalReference,
                ["correlationId"] = order.ExternalReference,
            };

            PreAuthSimulationResponse response = await preAuthService.HandleAsync(
                new PreAuthSimulationRequest(
                    siteCode,
                    "preauth-cancel",
                    HttpMethods.Post,
                    httpContext.Request.Path,
                    httpContext.TraceIdentifier,
                    requestBody,
                    fields),
                cancellationToken);

            order.Status = "CANCELLED";

            string message = ExtractMessage(response.Body) ?? "Order cancelled.";
            return Results.Json(
                new PetroniteSimCancelResponse(orderId, "CANCELLED", message),
                JsonOptions);
        });

        // GET /petronite/{siteCode}/direct-authorize-requests/pending — List pending orders
        group.MapGet("/direct-authorize-requests/pending", (
            string siteCode,
            PetroniteProtocolState protocolState,
            HttpContext httpContext) =>
        {
            if (!ValidateBearerToken(httpContext, protocolState))
            {
                return Results.Json(new PetroniteSimErrorResponse("Unauthorized"), statusCode: 401);
            }

            IReadOnlyList<PetroniteSimPendingOrder> pending = protocolState.Orders.Values
                .Where(o => o.SiteCode == siteCode && (o.Status == "PENDING" || o.Status == "AUTHORIZED"))
                .OrderBy(o => o.CreatedAt)
                .Select(o => new PetroniteSimPendingOrder(
                    o.OrderId,
                    o.NozzleId,
                    o.Status,
                    o.ExternalReference,
                    o.CreatedAt))
                .ToList();

            return Results.Json(pending, JsonOptions);
        });

        // GET /petronite/{siteCode}/nozzles/assigned — Nozzle assignments
        group.MapGet("/nozzles/assigned", (
            string siteCode,
            PetroniteProtocolState protocolState,
            PetroniteSimulatorState existingState,
            HttpContext httpContext) =>
        {
            if (!ValidateBearerToken(httpContext, protocolState))
            {
                return Results.Json(new PetroniteSimErrorResponse("Unauthorized"), statusCode: 401);
            }

            // Use existing Petronite simulator state's nozzle assignments
            IReadOnlyList<PetroniteNozzleAssignment> assignments = existingState.GetNozzleAssignments();

            IReadOnlyList<PetroniteSimNozzleAssignment> result = assignments
                .Select(a => new PetroniteSimNozzleAssignment(
                    $"NOZ-{a.PumpNumber}-{a.NozzleNumber}",
                    a.PumpNumber,
                    a.NozzleNumber,
                    a.ProductCode,
                    a.IsNozzleLifted ? "ACTIVE" : "IDLE"))
                .ToList();

            return Results.Json(result, JsonOptions);
        });

        return app;
    }

    private static bool ValidateBearerToken(HttpContext httpContext, PetroniteProtocolState protocolState)
    {
        string? authHeader = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string token = authHeader["Bearer ".Length..].Trim();
        return protocolState.ValidateToken(token);
    }

    private static (int PumpNumber, int NozzleNumber) ParseNozzleId(string nozzleId)
    {
        // Expected format: "NOZ-{pump}-{nozzle}"
        if (string.IsNullOrWhiteSpace(nozzleId))
        {
            return (1, 1);
        }

        string[] parts = nozzleId.Split('-');
        if (parts.Length >= 3 &&
            int.TryParse(parts[1], out int pump) &&
            int.TryParse(parts[2], out int nozzle))
        {
            return (pump, nozzle);
        }

        // Try simple numeric format
        if (int.TryParse(nozzleId, out int simple))
        {
            return (simple, 1);
        }

        return (1, 1);
    }

    private static string? ExtractMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;
        return ExtractField(responseBody, "message");
    }

    private static string? ExtractField(string? responseBody, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty(fieldName, out JsonElement el) && el.ValueKind == JsonValueKind.String)
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

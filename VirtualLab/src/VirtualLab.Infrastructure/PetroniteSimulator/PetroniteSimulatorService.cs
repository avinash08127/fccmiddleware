using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualLab.Infrastructure.PetroniteSimulator;

/// <summary>
/// HTTP server simulating the Petronite REST/JSON backend.
/// Provides OAuth2 token issuance, nozzle assignments, direct-authorize-requests,
/// and auto-webhook delivery on transaction completion.
/// </summary>
public sealed class PetroniteSimulatorService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly PetroniteSimulatorOptions _options;
    private readonly PetroniteSimulatorState _state;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PetroniteSimulatorService> _logger;

    public PetroniteSimulatorService(
        IOptions<PetroniteSimulatorOptions> options,
        PetroniteSimulatorState state,
        IHttpClientFactory httpClientFactory,
        ILogger<PetroniteSimulatorService> logger)
    {
        _options = options.Value;
        _state = state;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _state.Reset(_options.PumpCount);
        _logger.LogInformation(
            "Petronite simulator starting — port {Port}, pumps {Pumps}",
            _options.Port, _options.PumpCount);

        WebApplication app = BuildApp(stoppingToken);
        await app.RunAsync(stoppingToken);
    }

    private WebApplication BuildApp(CancellationToken stoppingToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [$"--urls=http://0.0.0.0:{_options.Port}"],
        });
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();

        // POST /oauth/token — OAuth2 token issuance
        app.MapPost("/oauth/token", (HttpContext httpContext) =>
        {
            if (!ValidateBasicAuth(httpContext))
            {
                return Results.Json(new { error = "invalid_client", error_description = "Invalid client credentials." }, statusCode: 401);
            }

            PetroniteOAuthToken token = _state.CreateToken(_options.TokenExpiresInSeconds);
            _logger.LogInformation("Petronite OAuth token issued: {Token}", token.AccessToken[..20] + "...");

            return Results.Ok(new
            {
                access_token = token.AccessToken,
                token_type = "Bearer",
                expires_in = _options.TokenExpiresInSeconds,
            });
        });

        // GET /nozzles/assigned — List nozzle assignments
        app.MapGet("/nozzles/assigned", (HttpContext httpContext) =>
        {
            if (!ValidateBearerToken(httpContext))
            {
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
            }

            IReadOnlyList<PetroniteNozzleAssignment> assignments = _state.GetNozzleAssignments();

            return Results.Ok(new
            {
                nozzles = assignments.Select(a => new
                {
                    pumpNumber = a.PumpNumber,
                    nozzleNumber = a.NozzleNumber,
                    productCode = a.ProductCode,
                    productName = a.ProductName,
                    isNozzleLifted = a.IsNozzleLifted,
                }),
            });
        });

        // POST /direct-authorize-requests/create — Create a pre-auth order
        app.MapPost("/direct-authorize-requests/create", async (HttpContext httpContext) =>
        {
            if (!ValidateBearerToken(httpContext))
            {
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
            }

            PetroniteCreateOrderRequest? request = await ReadJsonBodyAsync<PetroniteCreateOrderRequest>(httpContext);
            if (request is null)
            {
                return Results.BadRequest(new { error = "Invalid request body." });
            }

            PetroniteOrder order = _state.CreateOrder(request);
            _logger.LogInformation("Petronite order created: {OrderId} for pump {Pump}", order.Id, order.PumpNumber);

            return Results.Created($"/direct-authorize-requests/{order.Id}", new
            {
                id = order.Id,
                pumpNumber = order.PumpNumber,
                nozzleNumber = order.NozzleNumber,
                amount = order.Amount,
                status = order.Status.ToString().ToLowerInvariant(),
                createdAt = order.CreatedAtUtc,
            });
        });

        // POST /direct-authorize-requests/authorize — Authorize a pending order
        app.MapPost("/direct-authorize-requests/authorize", async (HttpContext httpContext) =>
        {
            if (!ValidateBearerToken(httpContext))
            {
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
            }

            AuthorizeRequest? request = await ReadJsonBodyAsync<AuthorizeRequest>(httpContext);
            if (request is null || string.IsNullOrWhiteSpace(request.OrderId))
            {
                return Results.BadRequest(new { error = "orderId is required." });
            }

            PetroniteOrder? order = _state.GetOrder(request.OrderId);
            if (order is null)
            {
                return Results.NotFound(new { error = $"Order '{request.OrderId}' not found." });
            }

            // Check nozzle lifted state
            PetroniteNozzleAssignment? nozzle = _state.GetNozzleAssignment(order.PumpNumber);
            if (nozzle is not null && !nozzle.IsNozzleLifted)
            {
                return Results.Json(new
                {
                    error = "nozzle_not_lifted",
                    message = $"Nozzle on pump {order.PumpNumber} is not lifted. Lift the nozzle before authorizing.",
                }, statusCode: 409);
            }

            if (!_state.TryAuthorizeOrder(request.OrderId, out PetroniteOrder? authorizedOrder) || authorizedOrder is null)
            {
                return Results.Json(new
                {
                    error = "invalid_state",
                    message = $"Order '{request.OrderId}' cannot be authorized from current state '{order.Status}'.",
                }, statusCode: 409);
            }

            _logger.LogInformation("Petronite order authorized: {OrderId}", authorizedOrder.Id);

            // Schedule auto-webhook delivery after configurable delay
            _ = Task.Run(async () =>
            {
                await Task.Delay(_options.AutoWebhookDelayMs, stoppingToken);
                await CompleteOrderAndSendWebhookAsync(authorizedOrder.Id, stoppingToken);
            }, stoppingToken);

            return Results.Ok(new
            {
                id = authorizedOrder.Id,
                pumpNumber = authorizedOrder.PumpNumber,
                status = authorizedOrder.Status.ToString().ToLowerInvariant(),
                authorizedAt = authorizedOrder.AuthorizedAtUtc,
            });
        });

        // POST /direct-authorize-requests/{id}/cancel — Cancel an order
        app.MapPost("/direct-authorize-requests/{id}/cancel", (string id, HttpContext httpContext) =>
        {
            if (!ValidateBearerToken(httpContext))
            {
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
            }

            if (!_state.TryCancelOrder(id, out PetroniteOrder? cancelledOrder) || cancelledOrder is null)
            {
                PetroniteOrder? existing = _state.GetOrder(id);
                if (existing is null)
                {
                    return Results.NotFound(new { error = $"Order '{id}' not found." });
                }

                return Results.Json(new
                {
                    error = "invalid_state",
                    message = $"Order '{id}' cannot be cancelled from current state '{existing.Status}'.",
                }, statusCode: 409);
            }

            _logger.LogInformation("Petronite order cancelled: {OrderId}", cancelledOrder.Id);

            return Results.Ok(new
            {
                id = cancelledOrder.Id,
                status = cancelledOrder.Status.ToString().ToLowerInvariant(),
                cancelledAt = cancelledOrder.CancelledAtUtc,
            });
        });

        // GET /direct-authorize-requests/pending — List pending orders
        app.MapGet("/direct-authorize-requests/pending", (HttpContext httpContext) =>
        {
            if (!ValidateBearerToken(httpContext))
            {
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
            }

            IReadOnlyList<PetroniteOrder> pending = _state.GetPendingOrders();
            return Results.Ok(new
            {
                orders = pending.Select(o => new
                {
                    id = o.Id,
                    pumpNumber = o.PumpNumber,
                    nozzleNumber = o.NozzleNumber,
                    amount = o.Amount,
                    status = o.Status.ToString().ToLowerInvariant(),
                    createdAt = o.CreatedAtUtc,
                    authorizedAt = o.AuthorizedAtUtc,
                }),
            });
        });

        return app;
    }

    // -----------------------------------------------------------------------
    // Auto-webhook delivery
    // -----------------------------------------------------------------------

    private async Task CompleteOrderAndSendWebhookAsync(string orderId, CancellationToken cancellationToken)
    {
        if (!_state.TryCompleteOrder(orderId, out PetroniteOrder? completedOrder) || completedOrder is null)
        {
            _logger.LogWarning("Petronite auto-webhook: order {OrderId} could not be completed", orderId);
            return;
        }

        _logger.LogInformation("Petronite order completed: {OrderId}", completedOrder.Id);

        if (string.IsNullOrWhiteSpace(_state.WebhookCallbackUrl))
        {
            _logger.LogDebug("Petronite auto-webhook skipped — no callback URL registered");
            return;
        }

        await SendWebhookAsync(completedOrder, cancellationToken);
    }

    /// <summary>Send transaction.completed webhook to registered callback URL.</summary>
    internal async Task SendWebhookAsync(PetroniteOrder completedOrder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_state.WebhookCallbackUrl))
        {
            return;
        }

        PetroniteNozzleAssignment? nozzle = _state.GetNozzleAssignment(completedOrder.PumpNumber);

        object webhookPayload = new
        {
            eventType = "transaction.completed",
            occurredAt = DateTimeOffset.UtcNow,
            data = new
            {
                orderId = completedOrder.Id,
                pumpNumber = completedOrder.PumpNumber,
                nozzleNumber = completedOrder.NozzleNumber,
                productCode = nozzle?.ProductCode ?? "UNL95",
                productName = nozzle?.ProductName ?? "Unleaded 95",
                volume = 10.00m,
                amount = completedOrder.Amount,
                unitPrice = completedOrder.Amount / 10.00m,
                customerName = completedOrder.CustomerName,
                customerTaxId = completedOrder.CustomerTaxId,
                customerTaxOffice = completedOrder.CustomerTaxOffice,
                completedAt = completedOrder.CompletedAtUtc,
            },
        };

        string payloadJson = JsonSerializer.Serialize(webhookPayload, JsonOptions);
        string hmacSignature = ComputeHmacSha256(payloadJson, _options.WebhookSecret);

        try
        {
            HttpClient client = _httpClientFactory.CreateClient("PetroniteSimulator");
            using HttpRequestMessage request = new(HttpMethod.Post, _state.WebhookCallbackUrl);
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            request.Headers.Add("X-Webhook-Signature", hmacSignature);
            request.Headers.Add("X-Webhook-Event", "transaction.completed");

            HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            _logger.LogInformation(
                "Petronite webhook sent to {Url} — {StatusCode}",
                _state.WebhookCallbackUrl, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Petronite webhook delivery to {Url} failed", _state.WebhookCallbackUrl);
        }
    }

    // -----------------------------------------------------------------------
    // Auth helpers
    // -----------------------------------------------------------------------

    private bool ValidateBasicAuth(HttpContext httpContext)
    {
        string? authHeader = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string encoded = authHeader["Basic ".Length..].Trim();
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            string[] parts = decoded.Split(':', 2);

            if (parts.Length != 2)
            {
                return false;
            }

            return string.Equals(parts[0], _options.ClientId, StringComparison.Ordinal)
                && string.Equals(parts[1], _options.ClientSecret, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private bool ValidateBearerToken(HttpContext httpContext)
    {
        string? authHeader = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string token = authHeader["Bearer ".Length..].Trim();
        return _state.ValidateToken(token);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string ComputeHmacSha256(string payload, string secret)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<T?> ReadJsonBodyAsync<T>(HttpContext httpContext) where T : class
    {
        try
        {
            return await httpContext.Request.ReadFromJsonAsync<T>();
        }
        catch
        {
            return null;
        }
    }
}

// -----------------------------------------------------------------------
// Internal request contracts
// -----------------------------------------------------------------------

internal sealed class AuthorizeRequest
{
    public string OrderId { get; set; } = string.Empty;
}

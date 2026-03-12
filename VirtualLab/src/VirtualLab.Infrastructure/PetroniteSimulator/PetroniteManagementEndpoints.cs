using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace VirtualLab.Infrastructure.PetroniteSimulator;

/// <summary>
/// REST management endpoints for the Petronite simulator.
/// Allows test harnesses to register webhooks, inject transactions,
/// control nozzle states, inspect state, and reset the simulator.
/// </summary>
public static class PetroniteManagementEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static IEndpointRouteBuilder MapPetroniteManagementEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/petronite");

        // POST /api/petronite/set-webhook-url — Register webhook callback
        group.MapPost("/set-webhook-url", (PetroniteSetWebhookRequest request, PetroniteSimulatorState state) =>
        {
            state.WebhookCallbackUrl = request.Url;

            return Results.Ok(new
            {
                message = "Webhook URL registered.",
                webhookUrl = state.WebhookCallbackUrl,
            });
        });

        // POST /api/petronite/inject-transaction — Trigger webhook with transaction
        group.MapPost("/inject-transaction", async (
            PetroniteInjectTransactionRequest request,
            PetroniteSimulatorState state,
            PetroniteSimulatorService simulatorService,
            CancellationToken cancellationToken) =>
        {
            // Create and immediately complete an order, then fire webhook
            PetroniteOrder order = state.CreateOrder(new PetroniteCreateOrderRequest
            {
                PumpNumber = request.PumpNumber ?? 1,
                NozzleNumber = request.NozzleNumber ?? 1,
                Amount = request.Amount ?? 18.50m,
                CustomerName = request.CustomerName ?? string.Empty,
                CustomerTaxId = request.CustomerTaxId ?? string.Empty,
                CustomerTaxOffice = request.CustomerTaxOffice ?? string.Empty,
            });

            // Fast-track through authorized to completed
            state.TryAuthorizeOrder(order.Id, out _);
            state.TryCompleteOrder(order.Id, out PetroniteOrder? completedOrder);

            if (completedOrder is not null)
            {
                await simulatorService.SendWebhookAsync(completedOrder, cancellationToken);
            }

            return Results.Created($"/api/petronite/state", new
            {
                message = "Transaction injected and webhook sent.",
                orderId = order.Id,
                status = completedOrder?.Status.ToString().ToLowerInvariant() ?? "created",
                webhookSent = completedOrder is not null && !string.IsNullOrWhiteSpace(state.WebhookCallbackUrl),
            });
        });

        // POST /api/petronite/set-nozzle-state — Control nozzle lifted state
        group.MapPost("/set-nozzle-state", (PetroniteSetNozzleStateRequest request, PetroniteSimulatorState state) =>
        {
            PetroniteNozzleAssignment? existing = state.GetNozzleAssignment(request.PumpNumber);
            if (existing is null)
            {
                return Results.NotFound(new { error = $"Pump {request.PumpNumber} not found." });
            }

            PetroniteNozzleAssignment updated = existing with
            {
                IsNozzleLifted = request.IsNozzleLifted,
                NozzleNumber = request.NozzleNumber ?? existing.NozzleNumber,
                ProductCode = request.ProductCode ?? existing.ProductCode,
                ProductName = request.ProductName ?? existing.ProductName,
            };

            state.SetNozzleAssignment(request.PumpNumber, updated);

            return Results.Ok(new
            {
                message = $"Pump {request.PumpNumber} nozzle state updated.",
                pumpNumber = updated.PumpNumber,
                isNozzleLifted = updated.IsNozzleLifted,
                productCode = updated.ProductCode,
            });
        });

        // GET /api/petronite/state — Get simulator state
        group.MapGet("/state", (PetroniteSimulatorState state) =>
        {
            return Results.Ok(state.ToSnapshot());
        });

        // POST /api/petronite/reset — Reset simulator state
        group.MapPost("/reset", (
            PetroniteSimulatorState state,
            Microsoft.Extensions.Options.IOptions<PetroniteSimulatorOptions> options) =>
        {
            state.Reset(options.Value.PumpCount);

            return Results.Ok(new
            {
                message = "Petronite simulator reset.",
                orderCount = state.OrderCount,
                nozzleCount = state.GetNozzleAssignments().Count,
                activeTokenCount = state.ActiveTokenCount,
            });
        });

        return app;
    }
}

// -----------------------------------------------------------------------
// Management request contracts
// -----------------------------------------------------------------------

public sealed class PetroniteSetWebhookRequest
{
    public string Url { get; set; } = string.Empty;
}

public sealed class PetroniteInjectTransactionRequest
{
    public int? PumpNumber { get; set; }
    public int? NozzleNumber { get; set; }
    public decimal? Amount { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerTaxId { get; set; }
    public string? CustomerTaxOffice { get; set; }
}

public sealed class PetroniteSetNozzleStateRequest
{
    public int PumpNumber { get; set; }
    public bool IsNozzleLifted { get; set; }
    public int? NozzleNumber { get; set; }
    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace VirtualLab.Infrastructure.AdvatecSimulator;

/// <summary>
/// REST management endpoints for the Advatec EFD simulator.
/// Allows test harnesses to configure webhook targets, inject receipts,
/// control delays and error modes, inspect state, and reset the simulator.
/// </summary>
public static class AdvatecManagementEndpoints
{
    public static IEndpointRouteBuilder MapAdvatecManagementEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/advatec");

        // POST /api/advatec/configure-webhook — Set webhook target URL and optional token
        group.MapPost("/configure-webhook", (AdvatecConfigureWebhookRequest request, AdvatecSimulatorState state) =>
        {
            state.WebhookCallbackUrl = request.Url;
            state.WebhookToken = request.Token;

            return Results.Ok(new
            {
                message = "Webhook configuration updated.",
                webhookUrl = state.WebhookCallbackUrl,
                hasToken = !string.IsNullOrWhiteSpace(state.WebhookToken),
            });
        });

        // POST /api/advatec/inject-receipt — Inject a receipt directly (bypasses Customer submission)
        group.MapPost("/inject-receipt", async (
            AdvatecInjectReceiptRequest request,
            AdvatecSimulatorService simulatorService,
            AdvatecSimulatorState state,
            CancellationToken cancellationToken) =>
        {
            await simulatorService.InjectReceiptAsync(request, cancellationToken);

            return Results.Created("/api/advatec/state", new
            {
                message = "Receipt injected and webhook sent.",
                generatedCount = state.GeneratedReceiptCount,
                webhookSent = !string.IsNullOrWhiteSpace(state.WebhookCallbackUrl),
            });
        });

        // POST /api/advatec/configure-delay — Set receipt generation delay
        group.MapPost("/configure-delay", (AdvatecConfigureDelayRequest request, AdvatecSimulatorState state) =>
        {
            state.ReceiptDelayOverrideMs = request.DelayMs;

            return Results.Ok(new
            {
                message = "Receipt delay updated.",
                delayMs = state.ReceiptDelayOverrideMs,
            });
        });

        // POST /api/advatec/set-error-mode — Simulate errors
        group.MapPost("/set-error-mode", (AdvatecSetErrorModeRequest request, AdvatecSimulatorState state) =>
        {
            state.ErrorMode = request.Mode;

            return Results.Ok(new
            {
                message = $"Error mode set to '{state.ErrorMode}'.",
                errorMode = state.ErrorMode.ToString(),
            });
        });

        // GET /api/advatec/state — Get simulator state snapshot
        group.MapGet("/state", (AdvatecSimulatorState state) =>
        {
            return Results.Ok(state.ToSnapshot());
        });

        // POST /api/advatec/reset — Reset simulator state
        group.MapPost("/reset", (
            AdvatecSimulatorState state,
            Microsoft.Extensions.Options.IOptions<AdvatecSimulatorOptions> options) =>
        {
            state.Reset(options.Value.PumpCount);

            return Results.Ok(new
            {
                message = "Advatec simulator reset.",
                pumpCount = state.GetAllPumps().Count,
                productCount = state.Products.Count,
                generatedReceiptCount = state.GeneratedReceiptCount,
            });
        });

        return app;
    }
}

// -----------------------------------------------------------------------
// Management request contracts
// -----------------------------------------------------------------------

public sealed class AdvatecConfigureWebhookRequest
{
    public string Url { get; set; } = string.Empty;
    public string? Token { get; set; }
}

public sealed class AdvatecInjectReceiptRequest
{
    public int? PumpNumber { get; set; }
    public decimal? Volume { get; set; }
    public int? CustIdType { get; set; }
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? ProductCode { get; set; }
}

public sealed class AdvatecConfigureDelayRequest
{
    public int? DelayMs { get; set; }
}

public sealed class AdvatecSetErrorModeRequest
{
    public AdvatecErrorMode Mode { get; set; } = AdvatecErrorMode.None;
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace VirtualLab.Infrastructure.RadixSimulator;

/// <summary>
/// REST management endpoints for the Radix FDC simulator.
/// Allows test harnesses to inject transactions, change modes,
/// inject errors, inspect state, and reset the simulator.
/// </summary>
public static class RadixManagementEndpoints
{
    public static IEndpointRouteBuilder MapRadixManagementEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/radix");

        // POST /api/radix/inject-transaction — Add transaction to FIFO buffer
        group.MapPost("/inject-transaction", async (
            RadixInjectTransactionRequest request,
            RadixSimulatorState state,
            RadixSimulatorService simulatorService,
            CancellationToken cancellationToken) =>
        {
            RadixSimulatedTransaction transaction = new()
            {
                PumpNumber = request.PumpNumber ?? 1,
                NozzleNumber = request.NozzleNumber ?? 1,
                ProductId = request.ProductId ?? 1,
                ProductName = request.ProductName ?? "UNLEADED 95",
                Volume = request.Volume ?? "10.00",
                Amount = request.Amount ?? "18.50",
                Price = request.Price ?? "1.850",
                FdcDate = request.FdcDate ?? DateTimeOffset.UtcNow.ToString("dd/MM/yyyy"),
                FdcTime = request.FdcTime ?? DateTimeOffset.UtcNow.ToString("HH:mm:ss"),
                EfdId = request.EfdId ?? "1",
                SaveNum = request.SaveNum ?? (state.TransactionCount + 1).ToString(),
            };

            state.EnqueueTransaction(transaction);

            // If in unsolicited mode, push immediately
            if (state.Mode == RadixOperatingMode.Unsolicited)
            {
                await simulatorService.PushUnsolicitedTransactionAsync(transaction, cancellationToken);
            }

            return Results.Created($"/api/radix/state", new
            {
                message = "Transaction injected.",
                transactionId = transaction.Id,
                bufferDepth = state.TransactionCount,
                mode = state.Mode.ToString(),
            });
        });

        // POST /api/radix/set-mode — Force mode change
        group.MapPost("/set-mode", (RadixSetModeRequest request, RadixSimulatorState state) =>
        {
            if (string.Equals(request.Mode, "UNSOLICITED", StringComparison.OrdinalIgnoreCase))
            {
                state.Mode = RadixOperatingMode.Unsolicited;
                if (!string.IsNullOrWhiteSpace(request.CallbackUrl))
                {
                    state.UnsolicitedCallbackUrl = request.CallbackUrl;
                }
            }
            else
            {
                state.Mode = RadixOperatingMode.OnDemand;
            }

            return Results.Ok(new
            {
                message = $"Mode set to {state.Mode}.",
                mode = state.Mode.ToString(),
                callbackUrl = state.UnsolicitedCallbackUrl,
            });
        });

        // POST /api/radix/inject-error — Configure error injection
        group.MapPost("/inject-error", (RadixInjectErrorRequest request, RadixSimulatorState state) =>
        {
            RadixErrorInjection injection = new()
            {
                Target = request.Target ?? "transaction",
                ErrorCode = request.ErrorCode ?? 255,
                ErrorMessage = request.ErrorMessage ?? "Injected error",
            };

            state.InjectError(injection);

            return Results.Ok(new
            {
                message = "Error injection queued.",
                target = injection.Target,
                errorCode = injection.ErrorCode,
                errorMessage = injection.ErrorMessage,
                pendingErrors = state.PendingErrorCount,
            });
        });

        // GET /api/radix/state — Get simulator state
        group.MapGet("/state", (RadixSimulatorState state) =>
        {
            return Results.Ok(state.ToSnapshot());
        });

        // POST /api/radix/reset — Reset simulator state
        group.MapPost("/reset", (RadixSimulatorState state, Microsoft.Extensions.Options.IOptions<RadixSimulatorOptions> options) =>
        {
            state.Reset(options.Value.PumpCount);

            return Results.Ok(new
            {
                message = "Radix simulator reset.",
                mode = state.Mode.ToString(),
                bufferDepth = state.TransactionCount,
                preAuthCount = state.PreAuthCount,
            });
        });

        return app;
    }
}

// -----------------------------------------------------------------------
// Management request contracts
// -----------------------------------------------------------------------

public sealed class RadixInjectTransactionRequest
{
    public int? PumpNumber { get; set; }
    public int? NozzleNumber { get; set; }
    public int? ProductId { get; set; }
    public string? ProductName { get; set; }
    public string? Volume { get; set; }
    public string? Amount { get; set; }
    public string? Price { get; set; }
    public string? FdcDate { get; set; }
    public string? FdcTime { get; set; }
    public string? EfdId { get; set; }
    public string? SaveNum { get; set; }
}

public sealed class RadixSetModeRequest
{
    public string Mode { get; set; } = "ON_DEMAND";
    public string? CallbackUrl { get; set; }
}

public sealed class RadixInjectErrorRequest
{
    /// <summary>"transaction" or "auth"</summary>
    public string? Target { get; set; }

    /// <summary>Error code to return (e.g. 251, 255, 256, 258, 260).</summary>
    public int? ErrorCode { get; set; }

    /// <summary>Error message text.</summary>
    public string? ErrorMessage { get; set; }
}

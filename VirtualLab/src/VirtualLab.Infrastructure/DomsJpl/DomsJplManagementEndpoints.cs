using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace VirtualLab.Infrastructure.DomsJpl;

/// <summary>
/// REST API endpoints for controlling the DOMS JPL simulator during tests.
/// All endpoints are mapped under /api/doms-jpl/.
/// </summary>
public static class DomsJplManagementEndpoints
{
    public static IEndpointRouteBuilder MapDomsJplManagementEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/doms-jpl");

        group.MapGet("/state", (DomsJplSimulatorState state) =>
        {
            DomsSimulatorSnapshot snapshot = state.GetSnapshot();
            return Results.Ok(snapshot);
        });

        group.MapPost("/inject-transaction", (InjectTransactionRequest request, DomsJplSimulatorState state) =>
        {
            SimulatedDomsTransaction transaction = new()
            {
                TransactionId = request.TransactionId ?? Guid.NewGuid().ToString("N"),
                PumpNumber = request.PumpNumber ?? 1,
                NozzleNumber = request.NozzleNumber ?? 1,
                ProductCode = request.ProductCode ?? "UNL95",
                Volume = request.Volume ?? 25.00m,
                Amount = request.Amount ?? 100.00m,
                UnitPrice = request.UnitPrice ?? 4.00m,
                CurrencyCode = request.CurrencyCode ?? "TRY",
                OccurredAtUtc = request.OccurredAtUtc ?? DateTimeOffset.UtcNow,
                TransactionSequence = request.TransactionSequence ?? 1,
                AttendantId = request.AttendantId,
                ReceiptText = request.ReceiptText,
            };

            state.InjectTransaction(transaction);

            return Results.Created($"/api/doms-jpl/state", new
            {
                message = "Transaction injected.",
                transactionId = transaction.TransactionId,
                pumpNumber = transaction.PumpNumber,
                amount = transaction.Amount,
                volume = transaction.Volume,
                bufferCount = state.GetTransactions().Count,
            });
        });

        group.MapPost("/set-pump-state", (SetPumpStateRequest request, DomsJplSimulatorState state) =>
        {
            if (!Enum.TryParse<DomsPumpState>(request.State, ignoreCase: true, out DomsPumpState pumpState) &&
                !(int.TryParse(request.State, out int stateInt) && Enum.IsDefined(typeof(DomsPumpState), stateInt) && (pumpState = (DomsPumpState)stateInt) == pumpState))
            {
                return Results.BadRequest(new
                {
                    message = $"Invalid pump state '{request.State}'. Valid states: {string.Join(", ", Enum.GetNames<DomsPumpState>())}",
                });
            }

            int pumpNumber = request.PumpNumber ?? 1;
            state.SetPumpState(pumpNumber, pumpState);

            return Results.Ok(new
            {
                message = $"Pump {pumpNumber} set to {pumpState}.",
                pumpNumber,
                state = pumpState.ToString(),
                stateCode = (int)pumpState,
            });
        });

        group.MapPost("/inject-error", (InjectErrorRequest request, DomsJplSimulatorState state) =>
        {
            DomsErrorInjection injection = new()
            {
                ResponseDelayMs = request.ResponseDelayMs ?? 0,
                SendMalformedFrame = request.SendMalformedFrame ?? false,
                DropConnectionAfterLogon = request.DropConnectionAfterLogon ?? false,
                SuppressHeartbeats = request.SuppressHeartbeats ?? false,
                RejectLogon = request.RejectLogon ?? false,
                RejectAuthorize = request.RejectAuthorize ?? false,
                ShotCount = request.ShotCount ?? 0,
                ShotsRemaining = request.ShotCount ?? 0,
            };

            state.ErrorInjection = injection;

            return Results.Ok(new
            {
                message = "Error injection configured.",
                injection,
            });
        });

        // ---- Phase 7: Peripheral push endpoints ----

        group.MapPost("/push-bna-report", async (
            PushBnaReportRequest request,
            DomsJplSimulatorService simulatorService,
            CancellationToken cancellationToken) =>
        {
            var payload = new
            {
                type = "EptBnaReport",
                terminalId = request.TerminalId ?? "BNA-01",
                notesAccepted = request.NotesAccepted ?? 0,
                occurredAtUtc = DateTimeOffset.UtcNow,
            };

            await simulatorService.SendUnsolicitedPushAsync("EptBnaReport", payload, cancellationToken);

            return Results.Accepted(value: new
            {
                message = "BNA report pushed to active clients.",
                payload,
            });
        });

        group.MapPost("/push-dispenser-install", async (
            PushDispenserInstallRequest request,
            DomsJplSimulatorService simulatorService,
            CancellationToken cancellationToken) =>
        {
            var payload = new
            {
                type = "DispenserInstallData",
                dispenserId = request.DispenserId ?? "DISP-01",
                model = request.Model ?? "Wayne Helix 6000",
                occurredAtUtc = DateTimeOffset.UtcNow,
            };

            await simulatorService.SendUnsolicitedPushAsync("DispenserInstallData", payload, cancellationToken);

            return Results.Accepted(value: new
            {
                message = "Dispenser install data pushed to active clients.",
                payload,
            });
        });

        group.MapPost("/push-ept-info", async (
            PushEptInfoRequest request,
            DomsJplSimulatorService simulatorService,
            CancellationToken cancellationToken) =>
        {
            var payload = new
            {
                type = "EptInfo",
                terminalId = request.TerminalId ?? "EPT-01",
                version = request.Version ?? "1.0.0",
                occurredAtUtc = DateTimeOffset.UtcNow,
            };

            await simulatorService.SendUnsolicitedPushAsync("EptInfo", payload, cancellationToken);

            return Results.Accepted(value: new
            {
                message = "EPT info pushed to active clients.",
                payload,
            });
        });

        // ---- Phase 7: Unsupervised transaction injection ----

        group.MapPost("/inject-unsupervised-transaction", (InjectTransactionRequest request, DomsJplSimulatorState state) =>
        {
            SimulatedDomsTransaction transaction = new()
            {
                TransactionId = request.TransactionId ?? Guid.NewGuid().ToString("N"),
                PumpNumber = request.PumpNumber ?? 1,
                NozzleNumber = request.NozzleNumber ?? 1,
                ProductCode = request.ProductCode ?? "UNL95",
                Volume = request.Volume ?? 25.00m,
                Amount = request.Amount ?? 100.00m,
                UnitPrice = request.UnitPrice ?? 4.00m,
                CurrencyCode = request.CurrencyCode ?? "TRY",
                OccurredAtUtc = request.OccurredAtUtc ?? DateTimeOffset.UtcNow,
                TransactionSequence = request.TransactionSequence ?? 1,
                AttendantId = request.AttendantId,
            };

            state.InjectUnsupervisedTransaction(transaction);

            return Results.Created($"/api/doms-jpl/state", new
            {
                message = "Unsupervised transaction injected.",
                transactionId = transaction.TransactionId,
                pumpNumber = transaction.PumpNumber,
                amount = transaction.Amount,
                volume = transaction.Volume,
                bufferCount = state.GetUnsupervisedTransactions().Count,
            });
        });

        // ---- Phase 7: Price set management ----

        group.MapPost("/set-prices", (SetPricesRequest request, DomsJplSimulatorState state) =>
        {
            if (request.Grades is { Count: > 0 })
            {
                foreach (var grade in request.Grades)
                {
                    if (!string.IsNullOrEmpty(grade.GradeId))
                    {
                        state.PriceSet.GradePrices[grade.GradeId] = new SimulatedGradePrice
                        {
                            GradeId = grade.GradeId,
                            GradeName = grade.GradeName ?? grade.GradeId,
                            PriceMinorUnits = grade.PriceMinorUnits ?? 0,
                            CurrencyCode = grade.CurrencyCode ?? "TRY",
                        };
                    }
                }

                state.PriceSet.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            return Results.Ok(new
            {
                message = "Price set updated.",
                priceSet = state.PriceSet,
            });
        });

        // ---- Phase 7: Pump totals management ----

        group.MapPost("/set-pump-totals", (SetPumpTotalsRequest request, DomsJplSimulatorState state) =>
        {
            int pumpNumber = request.PumpNumber ?? 1;
            state.PumpTotals[pumpNumber] = new SimulatedPumpTotals
            {
                PumpNumber = pumpNumber,
                TotalVolumeMicrolitres = request.TotalVolumeMicrolitres ?? 0,
                TotalAmountMinorUnits = request.TotalAmountMinorUnits ?? 0,
                LastUpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            return Results.Ok(new
            {
                message = $"Pump {pumpNumber} totals set.",
                pumpNumber,
                totalVolumeMicrolitres = request.TotalVolumeMicrolitres ?? 0,
                totalAmountMinorUnits = request.TotalAmountMinorUnits ?? 0,
            });
        });

        group.MapPost("/reset", (DomsJplSimulatorState state, IOptions<DomsJplSimulatorOptions> simulatorOptions) =>
        {
            int pumpCount = simulatorOptions.Value.PumpCount;
            state.Initialize(pumpCount);

            return Results.Ok(new
            {
                message = "Simulator state reset.",
                pumpCount,
                transactionCount = 0,
            });
        });

        group.MapPost("/push-notification", async (
            PushNotificationRequest request,
            DomsJplSimulatorState state,
            DomsJplSimulatorService simulatorService,
            CancellationToken cancellationToken) =>
        {
            string messageType = request.MessageType ?? "FpStatusChange";
            object pushPayload = request;

            // Apply the state change if it's a pump state change
            if (string.Equals(messageType, "FpStatusChange", StringComparison.OrdinalIgnoreCase) &&
                request.PumpNumber.HasValue &&
                !string.IsNullOrWhiteSpace(request.State))
            {
                if (Enum.TryParse<DomsPumpState>(request.State, ignoreCase: true, out DomsPumpState pumpState))
                {
                    state.SetPumpState(request.PumpNumber.Value, pumpState);
                }
            }

            // If it's a transaction-available notification, inject a transaction
            if (string.Equals(messageType, "TransactionAvailable", StringComparison.OrdinalIgnoreCase))
            {
                SimulatedDomsTransaction transaction = new()
                {
                    TransactionId = Guid.NewGuid().ToString("N"),
                    PumpNumber = request.PumpNumber ?? 1,
                    Amount = request.Amount ?? 100.00m,
                    Volume = request.Volume ?? 25.00m,
                };

                state.InjectTransaction(transaction);
                pushPayload = new
                {
                    messageType,
                    transaction.TransactionId,
                    transaction.PumpNumber,
                    transaction.NozzleNumber,
                    transaction.Amount,
                    transaction.Volume,
                    transaction.ProductCode,
                    transaction.UnitPrice,
                    transaction.CurrencyCode,
                    transaction.TransactionSequence,
                    transaction.OccurredAtUtc,
                };
            }

            await simulatorService.SendUnsolicitedPushAsync(messageType, pushPayload, cancellationToken);

            return Results.Accepted(value: new
            {
                message = $"Push notification '{messageType}' delivered to active clients when connected.",
                messageType,
                connectedClients = state.ConnectedClientCount,
            });
        });

        return app;
    }
}

// ----- Request DTOs for the management endpoints -----

public sealed class InjectTransactionRequest
{
    public string? TransactionId { get; init; }
    public int? PumpNumber { get; init; }
    public int? NozzleNumber { get; init; }
    public string? ProductCode { get; init; }
    public decimal? Volume { get; init; }
    public decimal? Amount { get; init; }
    public decimal? UnitPrice { get; init; }
    public string? CurrencyCode { get; init; }
    public DateTimeOffset? OccurredAtUtc { get; init; }
    public int? TransactionSequence { get; init; }
    public string? AttendantId { get; init; }
    public string? ReceiptText { get; init; }
}

public sealed class SetPumpStateRequest
{
    public int? PumpNumber { get; init; }
    public string State { get; init; } = "Idle";
}

public sealed class InjectErrorRequest
{
    public int? ResponseDelayMs { get; init; }
    public bool? SendMalformedFrame { get; init; }
    public bool? DropConnectionAfterLogon { get; init; }
    public bool? SuppressHeartbeats { get; init; }
    public bool? RejectLogon { get; init; }
    public bool? RejectAuthorize { get; init; }
    public int? ShotCount { get; init; }
}

public sealed class PushNotificationRequest
{
    public string? MessageType { get; init; }
    public int? PumpNumber { get; init; }
    public string? State { get; init; }
    public decimal? Amount { get; init; }
    public decimal? Volume { get; init; }
}

// ---- Phase 7: Peripheral push request DTOs ----

public sealed class PushBnaReportRequest
{
    public string? TerminalId { get; init; }
    public int? NotesAccepted { get; init; }
}

public sealed class PushDispenserInstallRequest
{
    public string? DispenserId { get; init; }
    public string? Model { get; init; }
}

public sealed class PushEptInfoRequest
{
    public string? TerminalId { get; init; }
    public string? Version { get; init; }
}

public sealed class SetPricesRequest
{
    public List<SetGradePriceItem>? Grades { get; init; }
}

public sealed class SetGradePriceItem
{
    public string? GradeId { get; init; }
    public string? GradeName { get; init; }
    public long? PriceMinorUnits { get; init; }
    public string? CurrencyCode { get; init; }
}

public sealed class SetPumpTotalsRequest
{
    public int? PumpNumber { get; init; }
    public long? TotalVolumeMicrolitres { get; init; }
    public long? TotalAmountMinorUnits { get; init; }
}

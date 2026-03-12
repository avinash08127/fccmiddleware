using FccDesktopAgent.Core.Ingestion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FccDesktopAgent.Api.Endpoints;

/// <summary>
/// Local transaction endpoints — exposes buffered transactions to Odoo POS over LAN.
/// Architecture rule #12: GET /api/v1/transactions must never depend on live FCC access.
/// p95 target for first page (limit &lt;= 50) with 30,000 buffered records: &lt;= 100 ms.
/// </summary>
internal static class TransactionEndpoints
{
    internal static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/transactions")
            .WithTags("Transactions");

        // GET /api/v1/transactions — list buffered transactions (cursor-based pagination)
        // DEA-2.x: inject IBufferQueryService and implement cursor pagination + filters
        group.MapGet("/", (
            string? cursor,
            int pageSize = 50,
            string? status = null,
            int? pumpNumber = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null) =>
            NotImplemented("GET /api/v1/transactions"))
            .WithName("listLocalTransactions");

        // GET /api/v1/transactions/{id} — get single transaction by middleware UUID
        // DEA-2.x: query buffer by primary key
        group.MapGet("/{id:guid}", (Guid id) =>
            NotImplemented("GET /api/v1/transactions/{id}"))
            .WithName("getLocalTransactionById");

        // POST /api/v1/transactions/{id}/acknowledge — stamp odooOrderId on a transaction
        // Idempotent: same odooOrderId → 200; different odooOrderId → 409 (DEA-2.x)
        group.MapPost("/{id:guid}/acknowledge", (Guid id) =>
            NotImplemented("POST /api/v1/transactions/{id}/acknowledge"))
            .WithName("acknowledgeLocalTransaction");

        // POST /api/v1/transactions/pull — on-demand FCC pull (DEA-2.7)
        // Serialized with background poller via SemaphoreSlim; never races cursor state.
        // pumpNumber in request body is informational only — all transactions since last cursor
        // are fetched so no data is lost for other pumps.
        group.MapPost("/pull", async (
            [FromBody] ManualPullRequest? request,
            IIngestionOrchestrator ingestion,
            CancellationToken ct) =>
        {
            var triggeredAt = DateTimeOffset.UtcNow;
            var result = await ingestion.ManualPullAsync(request?.PumpNumber, ct);

            return Results.Ok(new ManualPullResponse(
                NewCount: result.NewTransactionsBuffered,
                SkippedCount: result.DuplicatesSkipped,
                FetchCycles: result.FetchCycles,
                CursorAdvanced: result.FetchCycles > 0 &&
                                result.NewTransactionsBuffered + result.DuplicatesSkipped > 0,
                TriggeredAtUtc: triggeredAt));
        })
        .WithName("manualFccPull");

        return app;
    }

    private static IResult NotImplemented(string endpoint) =>
        Results.Json(
            new
            {
                errorCode = "NOT_IMPLEMENTED",
                message = $"{endpoint} is not yet implemented",
                traceId = (string?)null,
                timestamp = DateTimeOffset.UtcNow
            },
            statusCode: StatusCodes.Status501NotImplemented);
}

/// <summary>
/// Optional request body for POST /api/v1/transactions/pull.
/// <see cref="PumpNumber"/> is logged for diagnostics but does not restrict the fetch —
/// all transactions since the last cursor are always fetched.
/// </summary>
internal sealed record ManualPullRequest(int? PumpNumber = null);

/// <summary>
/// Response from POST /api/v1/transactions/pull.
/// </summary>
internal sealed record ManualPullResponse(
    int NewCount,
    int SkippedCount,
    int FetchCycles,
    bool CursorAdvanced,
    DateTimeOffset TriggeredAtUtc);

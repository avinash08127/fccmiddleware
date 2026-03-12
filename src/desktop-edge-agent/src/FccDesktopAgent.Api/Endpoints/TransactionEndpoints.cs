using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
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
        group.MapGet("/", async (
            string? cursor,
            int pageSize = 50,
            string? status = null,
            int? pumpNumber = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            TransactionBufferManager buffer = default!,
            CancellationToken ct = default) =>
        {
            if (pageSize is < 1 or > 200)
                return Results.Json(
                    new { errorCode = "VALIDATION_ERROR", message = "pageSize must be between 1 and 200", timestamp = DateTimeOffset.UtcNow },
                    statusCode: StatusCodes.Status400BadRequest);

            DateTimeOffset? parsedCursor = null;
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                if (!DateTimeOffset.TryParse(cursor, out var c))
                    return Results.Json(
                        new { errorCode = "VALIDATION_ERROR", message = "Invalid cursor format; expected ISO 8601 timestamp", timestamp = DateTimeOffset.UtcNow },
                        statusCode: StatusCodes.Status400BadRequest);
                parsedCursor = c;
            }

            var (items, nextCursor) = await buffer.GetPagedForLocalApiAsync(
                parsedCursor, pageSize, status, pumpNumber, from, to, ct);

            return Results.Ok(new TransactionListResponse(
                Items: items.Select(MapToDto).ToList(),
                NextCursor: nextCursor,
                Count: items.Count));
        })
        .WithName("listLocalTransactions");

        // GET /api/v1/transactions/{id} — get single transaction by middleware UUID
        group.MapGet("/{id:guid}", async (
            Guid id,
            TransactionBufferManager buffer,
            CancellationToken ct) =>
        {
            var tx = await buffer.GetByIdAsync(id.ToString(), ct);
            if (tx is null)
                return Results.Json(
                    new { errorCode = "NOT_FOUND", message = $"Transaction {id} not found", timestamp = DateTimeOffset.UtcNow },
                    statusCode: StatusCodes.Status404NotFound);

            return Results.Ok(MapToDto(tx));
        })
        .WithName("getLocalTransactionById");

        // POST /api/v1/transactions/{id}/acknowledge — stamp odooOrderId on a transaction
        // Idempotent: same odooOrderId → 200; different odooOrderId → 409
        group.MapPost("/{id:guid}/acknowledge", async (
            Guid id,
            [FromBody] AcknowledgeRequest request,
            TransactionBufferManager buffer,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.OdooOrderId))
                return Results.Json(
                    new { errorCode = "VALIDATION_ERROR", message = "odooOrderId is required", timestamp = DateTimeOffset.UtcNow },
                    statusCode: StatusCodes.Status400BadRequest);

            var result = await buffer.AcknowledgeAsync(id.ToString(), request.OdooOrderId.Trim(), ct);

            return result switch
            {
                AcknowledgeResult.Success => Results.Ok(new
                {
                    transactionId = id.ToString(),
                    odooOrderId = request.OdooOrderId.Trim(),
                    acknowledged = true,
                    timestamp = DateTimeOffset.UtcNow
                }),
                AcknowledgeResult.NotFound => Results.Json(
                    new { errorCode = "NOT_FOUND", message = $"Transaction {id} not found", timestamp = DateTimeOffset.UtcNow },
                    statusCode: StatusCodes.Status404NotFound),
                AcknowledgeResult.Conflict => Results.Json(
                    new { errorCode = "CONFLICT", message = $"Transaction {id} is already acknowledged with a different Odoo order ID", timestamp = DateTimeOffset.UtcNow },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(
                    new { errorCode = "INTERNAL_ERROR", message = "Unexpected acknowledge result", timestamp = DateTimeOffset.UtcNow },
                    statusCode: StatusCodes.Status500InternalServerError),
            };
        })
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

    private static TransactionDto MapToDto(BufferedTransaction tx) => new(
        Id: tx.Id,
        FccTransactionId: tx.FccTransactionId,
        SiteCode: tx.SiteCode,
        PumpNumber: tx.PumpNumber,
        NozzleNumber: tx.NozzleNumber,
        ProductCode: tx.ProductCode,
        VolumeMicrolitres: tx.VolumeMicrolitres,
        AmountMinorUnits: tx.AmountMinorUnits,
        UnitPriceMinorPerLitre: tx.UnitPriceMinorPerLitre,
        CurrencyCode: tx.CurrencyCode,
        StartedAtUtc: tx.StartedAt,
        CompletedAtUtc: tx.CompletedAt,
        FiscalReceiptNumber: tx.FiscalReceiptNumber,
        FccVendor: tx.FccVendor,
        AttendantId: tx.AttendantId,
        SyncStatus: tx.SyncStatus.ToString(),
        OdooOrderId: tx.OdooOrderId,
        AcknowledgedAtUtc: tx.AcknowledgedAt);
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

internal sealed record TransactionDto(
    string Id,
    string FccTransactionId,
    string SiteCode,
    int PumpNumber,
    int NozzleNumber,
    string ProductCode,
    long VolumeMicrolitres,
    long AmountMinorUnits,
    long UnitPriceMinorPerLitre,
    string CurrencyCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? FiscalReceiptNumber,
    string FccVendor,
    string? AttendantId,
    string SyncStatus,
    string? OdooOrderId,
    DateTimeOffset? AcknowledgedAtUtc);

internal sealed record TransactionListResponse(
    IReadOnlyList<TransactionDto> Items,
    string? NextCursor,
    int Count);

internal sealed record AcknowledgeRequest(string OdooOrderId);

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

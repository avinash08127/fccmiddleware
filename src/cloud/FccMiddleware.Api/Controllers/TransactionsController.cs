using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using FccMiddleware.Api.Auth;
using FccMiddleware.Application.Ingestion;
using FccMiddleware.Application.Transactions;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Ingestion;
using FccMiddleware.Contracts.Transactions;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FccMiddleware.Api.Controllers;

/// <summary>
/// Handles transaction ingestion, Edge Agent uploads, Odoo polling, and Odoo acknowledgement.
/// POST /api/v1/transactions/ingest       — single raw FCC payload (FCC API key auth, not yet enforced).
/// POST /api/v1/transactions/upload       — batch canonical upload from Edge Agent (device JWT).
/// GET  /api/v1/transactions              — Odoo poll: paginated PENDING transactions (Odoo API key).
/// POST /api/v1/transactions/acknowledge  — Odoo acknowledge: batch stamp PENDING → SYNCED_TO_ODOO (Odoo API key).
/// </summary>
[ApiController]
[Route("api/v1/transactions")]
public sealed class TransactionsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<TransactionsController> _logger;

    public TransactionsController(IMediator mediator, ILogger<TransactionsController> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    /// <summary>
    /// Ingests a single raw FCC transaction payload.
    /// </summary>
    /// <remarks>
    /// Returns 202 Accepted when the transaction is new and stored as PENDING.
    /// Returns 409 Conflict when the (fccTransactionId, siteCode) pair has already been ingested.
    /// Returns 400 Bad Request when payload validation fails.
    /// </remarks>
    /// <param name="request">FCC vendor, site code, capture timestamp and raw payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("ingest")]
    [AllowAnonymous] // FCC API key auth not yet implemented; will be replaced with [Authorize(Policy="FccApiKey")]
    [ProducesResponseType(typeof(IngestResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Ingest(
        [FromBody] IngestRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<FccVendor>(request.FccVendor, ignoreCase: true, out var vendor))
        {
            return BadRequest(BuildError(
                "VALIDATION.INVALID_VENDOR",
                $"Unknown FCC vendor '{request.FccVendor}'. Valid values: {string.Join(", ", Enum.GetNames<FccVendor>())}",
                retryable: false));
        }

        var correlationId = GetOrCreateCorrelationId();
        var rawPayload = request.RawPayload.GetRawText();

        var command = new IngestTransactionCommand
        {
            FccVendor = vendor,
            SiteCode = request.SiteCode,
            CapturedAt = request.CapturedAt,
            RawPayload = rawPayload,
            ContentType = "application/json",
            CorrelationId = correlationId
        };

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "SITE_NOT_FOUND"          => NotFound(BuildError(result.Error.Code, result.Error.Message)),
                "ADAPTER_NOT_REGISTERED"  => BadRequest(BuildError(result.Error.Code, result.Error.Message)),
                var code when code.StartsWith("VALIDATION.") =>
                    BadRequest(BuildError(result.Error.Code, result.Error.Message)),
                _ => StatusCode(StatusCodes.Status500InternalServerError,
                    BuildError("INTERNAL.UNEXPECTED", result.Error.Message, retryable: true))
            };
        }

        var value = result.Value!;

        if (value.IsDuplicate)
        {
            return Conflict(BuildError(
                "CONFLICT.DUPLICATE_TRANSACTION",
                "Transaction already ingested.",
                details: new { fccTransactionId = GetFccTransactionIdFromPayload(rawPayload), existingId = value.OriginalTransactionId },
                retryable: false));
        }

        return StatusCode(StatusCodes.Status202Accepted, new IngestResponse
        {
            TransactionId = value.TransactionId,
            Status = "PENDING",
            FuzzyMatchFlagged = value.FuzzyMatchFlagged
        });
    }

    /// <summary>
    /// Accepts a batch of pre-normalized canonical transactions from an Edge Agent.
    /// </summary>
    /// <remarks>
    /// Returns HTTP 200 with per-record outcomes regardless of individual duplicates.
    /// Each record outcome is ACCEPTED, DUPLICATE, or REJECTED.
    /// Authenticated via device JWT bearer token.
    /// </remarks>
    /// <param name="request">Batch of up to 500 pre-normalized canonical transaction records.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("upload")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Upload(
        [FromBody] UploadRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Transactions is { Count: 0 })
        {
            return BadRequest(BuildError("VALIDATION.EMPTY_BATCH", "Batch must contain at least one transaction."));
        }

        if (request.Transactions.Count > 500)
        {
            return BadRequest(BuildError("VALIDATION.BATCH_TOO_LARGE",
                $"Batch size {request.Transactions.Count} exceeds maximum of 500."));
        }

        // ── Extract validated JWT claims ──────────────────────────────────────
        var deviceId    = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub")
                       ?? string.Empty;
        var siteCode    = User.FindFirstValue("site") ?? string.Empty;
        var leiStr      = User.FindFirstValue("lei")  ?? string.Empty;

        if (!Guid.TryParse(leiStr, out var legalEntityId))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_LEI",
                "JWT 'lei' claim is not a valid UUID."));
        }

        var correlationId = GetOrCreateCorrelationId();

        var command = new UploadTransactionBatchCommand
        {
            Records        = request.Transactions.Select(r => new UploadTransactionItem
            {
                FccTransactionId       = r.FccTransactionId,
                SiteCode               = r.SiteCode,
                FccVendor              = r.FccVendor,
                PumpNumber             = r.PumpNumber,
                NozzleNumber           = r.NozzleNumber,
                ProductCode            = r.ProductCode,
                VolumeMicrolitres      = r.VolumeMicrolitres,
                AmountMinorUnits       = r.AmountMinorUnits,
                UnitPriceMinorPerLitre = r.UnitPriceMinorPerLitre,
                CurrencyCode           = r.CurrencyCode,
                StartedAt              = r.StartedAt,
                CompletedAt            = r.CompletedAt,
                FiscalReceiptNumber    = r.FiscalReceiptNumber,
                AttendantId            = r.AttendantId
            }).ToList(),
            LegalEntityId  = legalEntityId,
            DeviceSiteCode = siteCode,
            DeviceId       = deviceId,
            CorrelationId  = correlationId
        };

        var result = await _mediator.Send(command, cancellationToken);

        var response = new UploadResponse
        {
            Results        = result.Results.Select(r => new UploadRecordResult
            {
                FccTransactionId    = r.FccTransactionId,
                Outcome             = r.Outcome,
                TransactionId       = r.TransactionId,
                OriginalTransactionId = r.OriginalTransactionId,
                ErrorCode           = r.ErrorCode
            }).ToList(),
            AcceptedCount  = result.Results.Count(r => r.Outcome == "ACCEPTED"),
            DuplicateCount = result.Results.Count(r => r.Outcome == "DUPLICATE"),
            RejectedCount  = result.Results.Count(r => r.Outcome == "REJECTED")
        };

        return Ok(response);
    }

    /// <summary>
    /// Returns FCC transaction IDs for the authenticated device site that have been acknowledged by Odoo.
    /// </summary>
    /// <remarks>
    /// Authenticated via device JWT bearer token.
    /// Only records at the JWT site claim are considered, and only when status=SYNCED_TO_ODOO.
    /// The since filter is inclusive against SyncedToOdooAt.
    /// </remarks>
    /// <param name="since">Inclusive UTC lower bound for SyncedToOdooAt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("synced-status")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(typeof(SyncedStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncedStatus(
        [FromQuery] DateTimeOffset? since,
        CancellationToken cancellationToken = default)
    {
        if (!since.HasValue)
            return BadRequest(BuildError("VALIDATION.REQUIRED_SINCE",
                "Query parameter 'since' is required and must be a valid ISO 8601 UTC timestamp."));

        var siteCode = User.FindFirstValue("site");
        var leiStr   = User.FindFirstValue("lei");

        if (string.IsNullOrWhiteSpace(siteCode) || !Guid.TryParse(leiStr, out var legalEntityId))
            return Unauthorized(BuildError("UNAUTHORIZED", "Device JWT is missing required claims (site, lei)."));

        var result = await _mediator.Send(new GetSyncedTransactionIdsQuery
        {
            LegalEntityId = legalEntityId,
            SiteCode = siteCode,
            Since = since.Value
        }, cancellationToken);

        return Ok(new SyncedStatusResponse
        {
            FccTransactionIds = result.FccTransactionIds
        });
    }

    /// <summary>
    /// Returns a cursor-paginated page of PENDING transactions for Odoo to process.
    /// </summary>
    /// <remarks>
    /// Authenticated via Odoo API key in the X-Api-Key header.
    /// Only PENDING transactions are returned — DUPLICATE and ARCHIVED are never served.
    /// Results are ordered oldest-first by ingest time for reliable cursor resumption.
    /// Page size is clamped to [1, 100]. Default: 50.
    /// </remarks>
    /// <param name="siteCode">Optional: filter to a specific site.</param>
    /// <param name="pumpNumber">Optional: filter to a specific pump.</param>
    /// <param name="from">Optional: lower bound on CreatedAt (inclusive, UTC ISO 8601).</param>
    /// <param name="cursor">Opaque pagination cursor from a previous response's nextCursor.</param>
    /// <param name="pageSize">Number of records per page (1–100, default 50).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet]
    [Authorize(Policy = OdooApiKeyAuthOptions.PolicyName)]
    [ProducesResponseType(typeof(PollTransactionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTransactions(
        [FromQuery] string? siteCode,
        [FromQuery] int? pumpNumber,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        // legalEntityId is derived from the authenticated API key (set as 'lei' claim).
        var leiStr = User.FindFirstValue("lei");
        if (!Guid.TryParse(leiStr, out var legalEntityId))
            return Unauthorized(BuildError("UNAUTHORIZED", "Missing or invalid 'lei' claim."));

        if (pageSize is < 1 or > 100)
            return BadRequest(BuildError("VALIDATION.INVALID_PAGE_SIZE",
                "pageSize must be between 1 and 100."));

        var query = new PollTransactionsQuery
        {
            LegalEntityId = legalEntityId,
            SiteCode      = siteCode,
            PumpNumber    = pumpNumber,
            From          = from,
            Cursor        = cursor,
            PageSize      = pageSize
        };

        var result = await _mediator.Send(query, cancellationToken);

        var response = new PollTransactionsResponse
        {
            Data = result.Transactions.Select(t => new TransactionPollDto
            {
                Id                     = t.Id,
                FccTransactionId       = t.FccTransactionId,
                SiteCode               = t.SiteCode,
                PumpNumber             = t.PumpNumber,
                NozzleNumber           = t.NozzleNumber,
                ProductCode            = t.ProductCode,
                VolumeMicrolitres      = t.VolumeMicrolitres,
                AmountMinorUnits       = t.AmountMinorUnits,
                UnitPriceMinorPerLitre = t.UnitPriceMinorPerLitre,
                CurrencyCode           = t.CurrencyCode,
                StartedAt              = t.StartedAt,
                CompletedAt            = t.CompletedAt,
                CreatedAt              = t.CreatedAt,
                Status                 = t.Status,
                CorrelationId          = t.CorrelationId,
                FiscalReceiptNumber    = t.FiscalReceiptNumber,
                AttendantId            = t.AttendantId,
                IsStale                = t.IsStale,
                FccVendor              = t.FccVendor,
                IngestionSource        = t.IngestionSource
            }).ToList(),
            Meta = new PollPageMeta
            {
                PageSize   = result.Transactions.Count,
                HasMore    = result.HasMore,
                NextCursor = result.NextCursor,
                TotalCount = result.TotalCount
            }
        };

        return Ok(response);
    }

    /// <summary>
    /// Batch-acknowledges transactions as Odoo-processed, transitioning PENDING → SYNCED_TO_ODOO.
    /// </summary>
    /// <remarks>
    /// Authenticated via Odoo API key in the X-Api-Key header.
    /// Idempotent: re-acknowledging a transaction with the same odooOrderId returns ALREADY_ACKNOWLEDGED.
    /// Up to 500 items per request. Per-record outcomes: ACKNOWLEDGED, ALREADY_ACKNOWLEDGED, CONFLICT, NOT_FOUND, FAILED.
    /// </remarks>
    /// <param name="request">Batch of acknowledgement items (transactionId + odooOrderId).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("acknowledge")]
    [Authorize(Policy = OdooApiKeyAuthOptions.PolicyName)]
    [ProducesResponseType(typeof(AcknowledgeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Acknowledge(
        [FromBody] AcknowledgeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Acknowledgements is { Count: 0 })
            return BadRequest(BuildError("VALIDATION.EMPTY_BATCH", "Acknowledgements must contain at least one item."));

        if (request.Acknowledgements.Count > 500)
            return BadRequest(BuildError("VALIDATION.BATCH_TOO_LARGE",
                $"Batch size {request.Acknowledgements.Count} exceeds maximum of 500."));

        var leiStr = User.FindFirstValue("lei");
        if (!Guid.TryParse(leiStr, out var legalEntityId))
            return Unauthorized(BuildError("UNAUTHORIZED", "Missing or invalid 'lei' claim."));

        var command = new AcknowledgeTransactionsBatchCommand
        {
            LegalEntityId = legalEntityId,
            Items = request.Acknowledgements.Select(a => new AcknowledgeTransactionItem
            {
                TransactionId = a.Id,
                OdooOrderId   = a.OdooOrderId
            }).ToList()
        };

        var result = await _mediator.Send(command, cancellationToken);

        var response = new AcknowledgeResponse
        {
            Results = result.Results.Select(r => new AcknowledgeResult
            {
                Id      = r.TransactionId,
                Outcome = r.Outcome.ToString(),
                Error   = r.ErrorCode is null ? null : new AcknowledgeError
                {
                    Code    = r.ErrorCode,
                    Message = r.ErrorMessage ?? string.Empty
                }
            }).ToList(),
            SucceededCount = result.SucceededCount,
            FailedCount    = result.FailedCount
        };

        return Ok(response);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ErrorResponse BuildError(
        string errorCode,
        string message,
        object? details = null,
        bool retryable = false) =>
        new()
        {
            ErrorCode = errorCode,
            Message = message,
            Details = details,
            TraceId = Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier,
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Retryable = retryable
        };

    private Guid GetOrCreateCorrelationId()
    {
        if (HttpContext.Request.Headers.TryGetValue("X-Correlation-Id", out var header)
            && Guid.TryParse(header, out var parsed))
        {
            return parsed;
        }

        return Guid.NewGuid();
    }

    private static string? GetFccTransactionIdFromPayload(string rawPayload)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            if (doc.RootElement.TryGetProperty("transactionId", out var prop))
                return prop.GetString();
        }
        catch { /* best-effort */ }
        return null;
    }
}

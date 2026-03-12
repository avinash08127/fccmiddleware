using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using FccMiddleware.Api.Auth;
using FccMiddleware.Api.Infrastructure;
using FccMiddleware.Application.Common;
using FccMiddleware.Application.Ingestion;
using FccMiddleware.Application.Observability;
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
/// POST /api/v1/transactions/ingest       — raw FCC push payload envelope (FCC API key + HMAC auth).
/// POST /api/v1/transactions/upload       — batch canonical upload from Edge Agent (device JWT).
/// GET  /api/v1/transactions              — Odoo poll: paginated PENDING transactions (Odoo API key).
/// POST /api/v1/transactions/acknowledge  — Odoo acknowledge: batch stamp PENDING → SYNCED_TO_ODOO (Odoo API key).
/// </summary>
[ApiController]
[Route("api/v1/transactions")]
public sealed class TransactionsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ISiteFccConfigProvider _siteFccConfigProvider;
    private readonly ILogger<TransactionsController> _logger;
    private readonly IObservabilityMetrics _metrics;

    public TransactionsController(
        IMediator mediator,
        ISiteFccConfigProvider siteFccConfigProvider,
        ILogger<TransactionsController> logger,
        IObservabilityMetrics metrics)
    {
        _mediator = mediator;
        _siteFccConfigProvider = siteFccConfigProvider;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// Ingests an FCC push payload.
    /// </summary>
    /// <remarks>
    /// Returns 202 Accepted when a single transaction is new and stored as PENDING.
    /// Returns 409 Conflict when a single transaction's (fccTransactionId, siteCode) pair has already been ingested.
    /// Returns 200 OK with per-record outcomes when rawPayload contains a transactions[] batch.
    /// Returns 400 Bad Request when payload validation fails.
    /// </remarks>
    /// <param name="request">FCC vendor, site code, capture timestamp and raw payload envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("ingest")]
    [Authorize(Policy = FccHmacAuthOptions.PolicyName)]
    [ProducesResponseType(typeof(IngestBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(IngestResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
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

        var scopeValidationResult = await ValidateFccIngestScopeAsync(request.SiteCode, cancellationToken);
        if (scopeValidationResult is not null)
        {
            return scopeValidationResult;
        }

        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var rawPayload = request.RawPayload.GetRawText();

        if (TryGetBatchTransactions(request.RawPayload, out var transactionItems, out var batchError))
        {
            if (batchError is not null)
                return BadRequest(batchError);

            if (transactionItems.Count > 1)
            {
                return Ok(await IngestBatchAsync(
                    transactionItems,
                    vendor,
                    request.SiteCode,
                    request.CapturedAt,
                    correlationId,
                    cancellationToken));
            }
        }

        var result = await SendIngestCommandAsync(
            vendor,
            request.SiteCode,
            request.CapturedAt,
            rawPayload,
            correlationId,
            cancellationToken);

        return BuildSingleIngestResponse(result, request.SiteCode, vendor, rawPayload);
    }

    private async Task<IngestBatchResponse> IngestBatchAsync(
        IReadOnlyList<JsonElement> transactionItems,
        FccVendor vendor,
        string siteCode,
        DateTimeOffset capturedAt,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var results = new List<IngestBatchRecordResult>(transactionItems.Count);
        var acceptedCount = 0;
        var rejectedCount = 0;

        for (var index = 0; index < transactionItems.Count; index++)
        {
            var item = transactionItems[index];
            if (item.ValueKind != JsonValueKind.Object)
            {
                rejectedCount++;
                results.Add(new IngestBatchRecordResult
                {
                    RecordIndex = index,
                    Outcome = "REJECTED",
                    ErrorCode = "VALIDATION.INVALID_PAYLOAD",
                    ErrorMessage = "Each item in rawPayload.transactions must be a JSON object."
                });
                continue;
            }

            var itemPayload = item.GetRawText();
            var result = await SendIngestCommandAsync(
                vendor,
                siteCode,
                capturedAt,
                itemPayload,
                correlationId,
                cancellationToken);

            if (result.IsSuccess && !result.Value!.IsDuplicate)
            {
                acceptedCount++;
            }
            else if (result.IsFailure)
            {
                rejectedCount++;

                if (!result.Error!.Code.StartsWith("VALIDATION.", StringComparison.Ordinal))
                {
                    _metrics.RecordIngestionFailure(
                        "fcc-push",
                        result.Error.Code,
                        siteCode,
                        vendor.ToString());
                }
            }

            results.Add(MapBatchResult(index, item, result));
        }

        if (acceptedCount > 0)
        {
            _metrics.RecordIngestionSuccess("fcc-push", siteCode, vendor.ToString(), acceptedCount);
        }

        return new IngestBatchResponse
        {
            Results = results,
            AcceptedCount = acceptedCount,
            DuplicateCount = results.Count(r => r.Outcome == "DUPLICATE"),
            RejectedCount = rejectedCount
        };
    }

    private async Task<Result<IngestTransactionResult>> SendIngestCommandAsync(
        FccVendor vendor,
        string siteCode,
        DateTimeOffset capturedAt,
        string rawPayload,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var command = new IngestTransactionCommand
        {
            FccVendor = vendor,
            SiteCode = siteCode,
            CapturedAt = capturedAt,
            RawPayload = rawPayload,
            ContentType = "application/json",
            CorrelationId = correlationId
        };

        return await _mediator.Send(command, cancellationToken);
    }

    private IActionResult BuildSingleIngestResponse(
        Result<IngestTransactionResult> result,
        string siteCode,
        FccVendor vendor,
        string rawPayload)
    {
        if (result.IsFailure)
        {
            if (!result.Error!.Code.StartsWith("VALIDATION.", StringComparison.Ordinal))
            {
                _metrics.RecordIngestionFailure(
                    source: "fcc-push",
                    category: result.Error.Code,
                    siteCode: siteCode,
                    vendor: vendor.ToString());
            }

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

        _metrics.RecordIngestionSuccess("fcc-push", siteCode, vendor.ToString());

        return StatusCode(StatusCodes.Status202Accepted, new IngestResponse
        {
            TransactionId = value.TransactionId,
            Status = "PENDING",
            FuzzyMatchFlagged = value.FuzzyMatchFlagged
        });
    }

    private IngestBatchRecordResult MapBatchResult(
        int index,
        JsonElement transactionItem,
        Result<IngestTransactionResult> result)
    {
        var fccTransactionId = GetFccTransactionIdFromPayload(transactionItem.GetRawText());

        if (result.IsFailure)
        {
            return new IngestBatchRecordResult
            {
                RecordIndex = index,
                FccTransactionId = fccTransactionId,
                Outcome = "REJECTED",
                ErrorCode = result.Error!.Code,
                ErrorMessage = result.Error.Message
            };
        }

        var value = result.Value!;
        return new IngestBatchRecordResult
        {
            RecordIndex = index,
            FccTransactionId = fccTransactionId,
            Outcome = value.IsDuplicate ? "DUPLICATE" : "ACCEPTED",
            TransactionId = value.IsDuplicate ? null : value.TransactionId,
            OriginalTransactionId = value.OriginalTransactionId
        };
    }

    private bool TryGetBatchTransactions(
        JsonElement rawPayload,
        out IReadOnlyList<JsonElement> transactions,
        out ErrorResponse? error)
    {
        transactions = Array.Empty<JsonElement>();
        error = null;

        if (rawPayload.ValueKind != JsonValueKind.Object
            || !rawPayload.TryGetProperty("transactions", out var transactionArray))
        {
            return false;
        }

        if (transactionArray.ValueKind != JsonValueKind.Array)
        {
            error = BuildError(
                "VALIDATION.INVALID_PAYLOAD",
                "rawPayload.transactions must be a JSON array.");
            return true;
        }

        if (transactionArray.GetArrayLength() == 0)
        {
            error = BuildError(
                "VALIDATION.EMPTY_BATCH",
                "rawPayload.transactions must contain at least one transaction.");
            return true;
        }

        transactions = transactionArray.EnumerateArray().ToArray();
        return true;
    }

    private async Task<IActionResult?> ValidateFccIngestScopeAsync(
        string requestSiteCode,
        CancellationToken cancellationToken)
    {
        var scopedSiteCode = User.FindFirstValue("site");
        if (!string.IsNullOrWhiteSpace(scopedSiteCode)
            && !string.Equals(scopedSiteCode, requestSiteCode, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                BuildError(
                    "FORBIDDEN.SITE_SCOPE",
                    "Authenticated FCC credential is not permitted to ingest transactions for the requested site.",
                    new
                    {
                        credentialSite = scopedSiteCode,
                        requestSiteCode
                    }));
        }

        var scopedLei = User.FindFirstValue("lei");
        if (string.IsNullOrWhiteSpace(scopedLei))
        {
            return null;
        }

        if (!Guid.TryParse(scopedLei, out var credentialLegalEntityId))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                BuildError(
                    "FORBIDDEN.LEGAL_ENTITY_SCOPE",
                    "Authenticated FCC credential has an invalid legal-entity scope."));
        }

        var siteConfig = await _siteFccConfigProvider.GetBySiteCodeAsync(requestSiteCode, cancellationToken);
        if (siteConfig is null)
        {
            return NotFound(BuildError(
                "SITE_NOT_FOUND",
                $"No active FCC configuration found for site '{requestSiteCode}'."));
        }

        if (siteConfig.Value.LegalEntityId != credentialLegalEntityId)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                BuildError(
                    "FORBIDDEN.LEGAL_ENTITY_SCOPE",
                    "Authenticated FCC credential is not permitted to ingest transactions for the requested legal entity.",
                    new
                    {
                        credentialLegalEntityId,
                        requestSiteCode,
                        requestLegalEntityId = siteConfig.Value.LegalEntityId
                    }));
        }

        return null;
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

        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);

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
                FccCorrelationId       = r.FccCorrelationId,
                OdooOrderId            = r.OdooOrderId,
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

        if (response.AcceptedCount > 0)
        {
            _metrics.RecordIngestionSuccess("edge-upload", siteCode, "CANONICAL", response.AcceptedCount);
        }

        if (response.RejectedCount > 0)
        {
            _metrics.RecordIngestionFailure(
                "edge-upload",
                "UPLOAD.REJECTED",
                siteCode,
                "CANONICAL",
                response.RejectedCount);
        }

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

        var stopwatch = Stopwatch.StartNew();
        var result = await _mediator.Send(query, cancellationToken);
        stopwatch.Stop();
        _metrics.RecordOdooPollLatency(legalEntityId, stopwatch.Elapsed.TotalMilliseconds, result.Transactions.Count);

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

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["eventType"] = "transaction_acknowledge_batch",
            ["legalEntityId"] = legalEntityId
        });

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

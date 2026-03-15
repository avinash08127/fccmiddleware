using System.Text.Json;
using FccMiddleware.Application.DeadLetter;
using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.Ingestion;

/// <summary>
/// Handles UploadTransactionBatchCommand.
/// Pipeline (per record): site-claim validation → vendor parse → within-batch dedup
///   → primary dedup (Redis → DB) → build entity → persist + outbox event → Redis cache.
/// Each record is processed and saved individually so partial batches succeed cleanly.
/// Race conditions are handled by catching unique constraint violations.
/// </summary>
public sealed class UploadTransactionBatchHandler
    : IRequestHandler<UploadTransactionBatchCommand, UploadTransactionBatchResult>
{
    private const int DefaultDedupWindowDays = 90;

    private readonly IDeduplicationService _deduplicationService;
    private readonly IIngestDbContext _db;
    private readonly ReconciliationMatchingService _reconciliationMatchingService;
    private readonly IDeadLetterService _deadLetterService;
    private readonly ILogger<UploadTransactionBatchHandler> _logger;

    public UploadTransactionBatchHandler(
        IDeduplicationService deduplicationService,
        IIngestDbContext db,
        ReconciliationMatchingService reconciliationMatchingService,
        IDeadLetterService deadLetterService,
        ILogger<UploadTransactionBatchHandler> logger)
    {
        _deduplicationService = deduplicationService;
        _db = db;
        _reconciliationMatchingService = reconciliationMatchingService;
        _deadLetterService = deadLetterService;
        _logger = logger;
    }

    public async Task<UploadTransactionBatchResult> Handle(
        UploadTransactionBatchCommand command,
        CancellationToken cancellationToken)
    {
        // ── Batch-level idempotency (GAP-4) ─────────────────────────────────────
        // If the edge provided an uploadBatchId, check Redis for a cached result.
        if (!string.IsNullOrEmpty(command.UploadBatchId))
        {
            var cached = await _deduplicationService.GetBatchResultAsync(command.UploadBatchId, cancellationToken);
            if (cached is not null)
            {
                _logger.LogInformation(
                    "Batch cache hit for batchId={BatchId} device={DeviceId} — returning cached result ({Count} records)",
                    command.UploadBatchId, command.DeviceId, cached.Results.Count);
                return cached;
            }
        }

        var results = new List<SingleUploadResult>(command.Records.Count);

        // Track (fccTransactionId:siteCode) keys accepted within this batch to detect
        // within-batch duplicates without an extra DB round-trip per record.
        var acceptedInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in command.Records)
        {
            // ── Step 1: Site-claim validation ─────────────────────────────────────
            if (!string.Equals(record.SiteCode, command.DeviceSiteCode, StringComparison.OrdinalIgnoreCase))
            {
                var msg = $"Transaction siteCode '{record.SiteCode}' does not match device site '{command.DeviceSiteCode}'.";
                await _deadLetterService.CreateAsync(
                    command.LegalEntityId, record.SiteCode,
                    DeadLetterType.TRANSACTION, DeadLetterReason.VALIDATION_FAILURE,
                    "SITE_MISMATCH", msg,
                    fccTransactionId: record.FccTransactionId,
                    rawPayloadJson: JsonSerializer.Serialize(record),
                    cancellationToken: cancellationToken);
                results.Add(Rejected(record.FccTransactionId, "SITE_MISMATCH", msg));
                continue;
            }

            // ── Step 2: FCC vendor parse ───────────────────────────────────────────
            if (!Enum.TryParse<FccVendor>(record.FccVendor, ignoreCase: true, out var vendor))
            {
                var msg = $"Unknown FCC vendor '{record.FccVendor}'.";
                await _deadLetterService.CreateAsync(
                    command.LegalEntityId, record.SiteCode,
                    DeadLetterType.TRANSACTION, DeadLetterReason.VALIDATION_FAILURE,
                    "INVALID_VENDOR", msg,
                    fccTransactionId: record.FccTransactionId,
                    rawPayloadJson: JsonSerializer.Serialize(record),
                    cancellationToken: cancellationToken);
                results.Add(Rejected(record.FccTransactionId, "INVALID_VENDOR", msg));
                continue;
            }

            // ── Step 3: Within-batch duplicate check ──────────────────────────────
            var dedupKey = $"{record.FccTransactionId}:{record.SiteCode}";
            if (acceptedInBatch.Contains(dedupKey))
            {
                results.Add(Duplicate(record.FccTransactionId, null));
                continue;
            }

            // ── Step 4: Primary dedup check (Redis → PostgreSQL) ──────────────────
            var existingId = await _deduplicationService.FindExistingAsync(
                record.FccTransactionId, record.SiteCode, cancellationToken);

            if (existingId is not null)
            {
                _logger.LogDebug(
                    "Batch upload duplicate {FccTransactionId}/{SiteCode}; device={DeviceId} original={ExistingId}",
                    record.FccTransactionId, record.SiteCode, command.DeviceId, existingId);

                results.Add(Duplicate(record.FccTransactionId, existingId));
                continue;
            }

            // ── Step 5: Build PENDING transaction entity ──────────────────────────
            var transaction = new Transaction
            {
                Id                     = Guid.NewGuid(),
                CreatedAt              = DateTimeOffset.UtcNow,
                UpdatedAt              = DateTimeOffset.UtcNow,
                LegalEntityId          = command.LegalEntityId,
                FccTransactionId       = record.FccTransactionId,
                SiteCode               = record.SiteCode,
                PumpNumber             = record.PumpNumber,
                NozzleNumber           = record.NozzleNumber,
                ProductCode            = record.ProductCode,
                VolumeMicrolitres      = record.VolumeMicrolitres,
                AmountMinorUnits       = record.AmountMinorUnits,
                UnitPriceMinorPerLitre = record.UnitPriceMinorPerLitre,
                CurrencyCode           = record.CurrencyCode,
                StartedAt              = record.StartedAt,
                CompletedAt            = record.CompletedAt,
                FccCorrelationId       = record.FccCorrelationId,
                FccVendor              = vendor,
                FiscalReceiptNumber    = record.FiscalReceiptNumber,
                AttendantId            = record.AttendantId,
                Status                 = TransactionStatus.PENDING,
                IngestionSource        = IngestionSource.EDGE_UPLOAD,
                OdooOrderId            = record.OdooOrderId,
                CorrelationId          = command.CorrelationId,
                SchemaVersion          = 1
            };

            // ── Step 6: Persist + outbox event (atomic per record) ────────────────
            _db.AddTransaction(transaction);
            _db.AddOutboxMessage(BuildIngestedOutboxMessage(transaction, command.CorrelationId));

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (IsUniqueConstraintViolation(ex))
            {
                // Race condition: another request inserted the same (fccTransactionId, siteCode).
                _db.ClearTracked();

                _logger.LogInformation(
                    "Unique constraint race on {FccTransactionId}/{SiteCode} during batch upload; treating as duplicate.",
                    record.FccTransactionId, record.SiteCode);

                var racedId = await _db.FindTransactionByDedupKeyAsync(
                    record.FccTransactionId, record.SiteCode, cancellationToken);

                results.Add(Duplicate(record.FccTransactionId, racedId));
                continue;
            }

            await _reconciliationMatchingService.MatchAsync(transaction, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            // ── Step 7: Populate Redis cache + track accepted ─────────────────────
            await _deduplicationService.SetCacheAsync(
                record.FccTransactionId, record.SiteCode,
                transaction.Id, DefaultDedupWindowDays, cancellationToken);

            acceptedInBatch.Add(dedupKey);

            results.Add(new SingleUploadResult
            {
                FccTransactionId = record.FccTransactionId,
                Outcome          = "ACCEPTED",
                TransactionId    = transaction.Id
            });
        }

        _logger.LogInformation(
            "Batch upload from device {DeviceId} ({Total} records): {Accepted} accepted, {Duplicate} duplicate, {Rejected} rejected",
            command.DeviceId, command.Records.Count,
            results.Count(r => r.Outcome == "ACCEPTED"),
            results.Count(r => r.Outcome == "DUPLICATE"),
            results.Count(r => r.Outcome == "REJECTED"));

        var batchResult = new UploadTransactionBatchResult { Results = results };

        // ── Cache batch result for idempotent retries (GAP-4) ────────────────────
        if (!string.IsNullOrEmpty(command.UploadBatchId))
        {
            await _deduplicationService.SetBatchResultAsync(command.UploadBatchId, batchResult, cancellationToken);
        }

        return batchResult;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static SingleUploadResult Rejected(string fccTransactionId, string errorCode, string errorMessage) =>
        new() { FccTransactionId = fccTransactionId, Outcome = "REJECTED", ErrorCode = errorCode, ErrorMessage = errorMessage };

    private static SingleUploadResult Duplicate(string fccTransactionId, Guid? originalId) =>
        new() { FccTransactionId = fccTransactionId, Outcome = "DUPLICATE", OriginalTransactionId = originalId };

    private static OutboxMessage BuildIngestedOutboxMessage(Transaction tx, Guid correlationId) =>
        new()
        {
            EventType     = "TransactionIngested",
            Payload       = JsonSerializer.Serialize(new
            {
                transactionId        = tx.Id,
                fccTransactionId     = tx.FccTransactionId,
                fccVendor            = tx.FccVendor.ToString(),
                pumpNumber           = tx.PumpNumber,
                totalAmountMinorUnits = tx.AmountMinorUnits,
                currencyCode         = tx.CurrencyCode,
                siteCode             = tx.SiteCode,
                legalEntityId        = tx.LegalEntityId,
                ingestionSource      = tx.IngestionSource.ToString()
            }),
            CorrelationId = correlationId,
            CreatedAt     = DateTimeOffset.UtcNow
        };

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("23505")
            || message.Contains("ix_transactions_dedup")
            || message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);
    }
}

using System.Text.Json;
using FccMiddleware.Application.Common;
using FccMiddleware.Application.DeadLetter;
using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Exceptions;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Models.Adapter;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.Ingestion;

/// <summary>
/// Handles IngestTransactionCommand.
/// Pipeline: site config lookup → adapter resolve → validate → normalize
///           → primary dedup (Redis → DB) → secondary fuzzy match → build entity
///           → S3 archive → persist + outbox event → populate Redis cache.
/// Race conditions are handled by catching unique constraint violations.
/// </summary>
public sealed class IngestTransactionHandler
    : IRequestHandler<IngestTransactionCommand, Result<IngestTransactionResult>>
{
    // Platform default per tier-2-2-deduplication-strategy.md §3.1
    private const int DefaultDedupWindowDays = 90;
    private const int FuzzyMatchWindowSeconds = 5;

    private readonly IFccAdapterFactory _adapterFactory;
    private readonly ISiteFccConfigProvider _siteFccConfigProvider;
    private readonly IDeduplicationService _deduplicationService;
    private readonly IIngestDbContext _db;
    private readonly ReconciliationMatchingService _reconciliationMatchingService;
    private readonly IDeadLetterService _deadLetterService;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<IngestTransactionHandler> _logger;

    public IngestTransactionHandler(
        IFccAdapterFactory adapterFactory,
        ISiteFccConfigProvider siteFccConfigProvider,
        IDeduplicationService deduplicationService,
        IIngestDbContext db,
        ReconciliationMatchingService reconciliationMatchingService,
        IDeadLetterService deadLetterService,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<IngestTransactionHandler> logger)
    {
        _adapterFactory = adapterFactory;
        _siteFccConfigProvider = siteFccConfigProvider;
        _deduplicationService = deduplicationService;
        _db = db;
        _reconciliationMatchingService = reconciliationMatchingService;
        _deadLetterService = deadLetterService;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    public async Task<Result<IngestTransactionResult>> Handle(
        IngestTransactionCommand command,
        CancellationToken cancellationToken)
    {
        // ── Step 1: Resolve site config + tenant ──────────────────────────────
        var siteResult = await _siteFccConfigProvider.GetBySiteCodeAsync(command.SiteCode, cancellationToken);
        if (siteResult is null)
        {
            return Result<IngestTransactionResult>.Failure(
                "SITE_NOT_FOUND",
                $"No active FCC configuration found for site '{command.SiteCode}'.");
        }

        var (siteFccConfig, legalEntityId) = siteResult.Value;

        // ── Step 2: Resolve adapter ───────────────────────────────────────────
        IFccAdapter adapter;
        try
        {
            adapter = _adapterFactory.Resolve(command.FccVendor, siteFccConfig);
        }
        catch (AdapterNotRegisteredException ex)
        {
            await _deadLetterService.CreateAsync(
                legalEntityId, command.SiteCode,
                DeadLetterType.TRANSACTION, DeadLetterReason.ADAPTER_ERROR,
                "ADAPTER_NOT_REGISTERED", ex.Message,
                rawPayloadJson: command.RawPayload,
                cancellationToken: cancellationToken);
            return Result<IngestTransactionResult>.Failure("ADAPTER_NOT_REGISTERED", ex.Message);
        }

        // ── Step 3: Build raw payload envelope ────────────────────────────────
        var envelope = new RawPayloadEnvelope
        {
            Vendor = command.FccVendor,
            SiteCode = command.SiteCode,
            ReceivedAtUtc = command.CapturedAt,
            ContentType = command.ContentType,
            Payload = command.RawPayload
        };

        // ── Step 4: Validate payload ──────────────────────────────────────────
        var validation = adapter.ValidatePayload(envelope);
        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "Payload validation failed for site {SiteCode} vendor {Vendor}: {ErrorCode} — {Message}",
                command.SiteCode, command.FccVendor, validation.ErrorCode, validation.Message);

            await _deadLetterService.CreateAsync(
                legalEntityId, command.SiteCode,
                DeadLetterType.TRANSACTION, DeadLetterReason.VALIDATION_FAILURE,
                $"VALIDATION.{validation.ErrorCode}",
                validation.Message ?? "Payload validation failed.",
                rawPayloadJson: command.RawPayload,
                cancellationToken: cancellationToken);

            return Result<IngestTransactionResult>.Failure(
                $"VALIDATION.{validation.ErrorCode}",
                validation.Message ?? "Payload validation failed.");
        }

        // ── Step 5: Normalize ─────────────────────────────────────────────────
        // M-06: Wrap normalization in try-catch so exceptions (e.g., from race between
        // validate and normalize, or edge cases not caught by validation) go to the
        // dead-letter queue instead of propagating as unhandled 500s.
        CanonicalTransaction canonical;
        try
        {
            canonical = adapter.NormalizeTransaction(envelope);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException)
        {
            _logger.LogWarning(ex,
                "Normalization failed for site {SiteCode} vendor {Vendor}: {Message}",
                command.SiteCode, command.FccVendor, ex.Message);

            await _deadLetterService.CreateAsync(
                legalEntityId, command.SiteCode,
                DeadLetterType.TRANSACTION, DeadLetterReason.NORMALIZATION_FAILURE,
                "NORMALIZATION_ERROR",
                $"Normalization failed: {ex.Message}",
                rawPayloadJson: command.RawPayload,
                cancellationToken: cancellationToken);

            return Result<IngestTransactionResult>.Failure(
                "NORMALIZATION_ERROR",
                $"Normalization failed: {ex.Message}");
        }

        // ── Step 6: Primary dedup check (Redis → PostgreSQL) ─────────────────
        var existingId = await _deduplicationService.FindExistingAsync(
            canonical.FccTransactionId, canonical.SiteCode, cancellationToken);

        if (existingId is not null)
        {
            _logger.LogInformation(
                "Duplicate transaction {FccTransactionId} for site {SiteCode}; original={ExistingId}",
                canonical.FccTransactionId, canonical.SiteCode, existingId);

            await WriteDeduplicatedEventAsync(canonical, legalEntityId, existingId.Value, command.CorrelationId, cancellationToken);

            return Result<IngestTransactionResult>.Success(new IngestTransactionResult
            {
                TransactionId = Guid.Empty,
                IsDuplicate = true,
                OriginalTransactionId = existingId
            });
        }

        // ── Step 7: Secondary fuzzy match review flag ─────────────────────────
        var fuzzyMatchFlagged = await _db.HasFuzzyMatchAsync(
            legalEntityId,
            canonical.SiteCode,
            canonical.PumpNumber,
            canonical.NozzleNumber,
            canonical.AmountMinorUnits,
            canonical.CompletedAt.AddSeconds(-FuzzyMatchWindowSeconds),
            canonical.CompletedAt.AddSeconds(FuzzyMatchWindowSeconds),
            cancellationToken);

        if (fuzzyMatchFlagged)
        {
            _logger.LogInformation(
                "Fuzzy duplicate review flagged for {FccTransactionId} at site {SiteCode} pump {PumpNumber} nozzle {NozzleNumber}",
                canonical.FccTransactionId,
                canonical.SiteCode,
                canonical.PumpNumber,
                canonical.NozzleNumber);
        }

        // ── Step 8: Build new PENDING transaction entity ──────────────────────
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LegalEntityId = legalEntityId,
            FccTransactionId = canonical.FccTransactionId,
            SiteCode = canonical.SiteCode,
            PumpNumber = canonical.PumpNumber,
            NozzleNumber = canonical.NozzleNumber,
            ProductCode = canonical.ProductCode,
            VolumeMicrolitres = canonical.VolumeMicrolitres,
            AmountMinorUnits = canonical.AmountMinorUnits,
            UnitPriceMinorPerLitre = canonical.UnitPriceMinorPerLitre,
            CurrencyCode = canonical.CurrencyCode,
            StartedAt = canonical.StartedAt,
            CompletedAt = canonical.CompletedAt,
            FccCorrelationId = canonical.FccCorrelationId,
            FccVendor = canonical.FccVendor,
            FiscalReceiptNumber = canonical.FiscalReceiptNumber,
            AttendantId = canonical.AttendantId,
            Status = TransactionStatus.PENDING,
            IngestionSource = command.IngestionSource,
            OdooOrderId = canonical.OdooOrderId,
            CorrelationId = command.CorrelationId,
            ReconciliationStatus = fuzzyMatchFlagged ? ReconciliationStatus.REVIEW_FUZZY_MATCH : null,
            SchemaVersion = 1
        };

        // ── Step 9: S3 archival deferred to background (TX-P06) ──────────────
        // RawPayloadRef is populated asynchronously after the transaction is saved.

        // ── Step 10: Persist transaction + reconciliation + outbox (single DB transaction) ──
        _db.AddTransaction(transaction);
        _db.AddOutboxMessage(BuildIngestedOutboxMessage(transaction, command.CorrelationId));

        // Run reconciliation matching before SaveChangesAsync so the transaction,
        // its reconciliation result, and the outbox event are all committed atomically.
        // This prevents reconciliation data loss if the process crashes mid-pipeline.
        await _reconciliationMatchingService.MatchAsync(transaction, cancellationToken);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex))
        {
            // Race condition: concurrent request inserted the same (fccTransactionId, siteCode).
            _db.ClearTracked();

            _logger.LogInformation(
                "Unique constraint race on {FccTransactionId}/{SiteCode}; treating as duplicate.",
                canonical.FccTransactionId, canonical.SiteCode);

            var originalId = await _db.FindTransactionByDedupKeyAsync(
                canonical.FccTransactionId, canonical.SiteCode, cancellationToken);

            // Propagate fuzzy match flag to the winning transaction if this request detected one
            if (fuzzyMatchFlagged && originalId.HasValue)
            {
                await _db.FlagFuzzyMatchAsync(originalId.Value, cancellationToken);
            }

            return Result<IngestTransactionResult>.Success(new IngestTransactionResult
            {
                TransactionId = Guid.Empty,
                IsDuplicate = true,
                OriginalTransactionId = originalId
            });
        }

        // ── Step 11: Populate Redis cache after successful DB commit ──────────
        await _deduplicationService.SetCacheAsync(
            canonical.FccTransactionId, canonical.SiteCode,
            transaction.Id, DefaultDedupWindowDays, cancellationToken);

        // TX-P06: Fire-and-forget S3 archival so it doesn't block the response.
        // RawPayloadRef is updated on the saved transaction row once archival completes.
        _ = Task.Run(() => ArchiveRawPayloadInBackgroundAsync(
            legalEntityId, command, canonical, transaction.Id));

        _logger.LogInformation(
            "Transaction {TransactionId} ingested as PENDING for site {SiteCode} vendor {Vendor}",
            transaction.Id, canonical.SiteCode, canonical.FccVendor);

        return Result<IngestTransactionResult>.Success(new IngestTransactionResult
        {
            TransactionId = transaction.Id,
            IsDuplicate = false,
            FuzzyMatchFlagged = fuzzyMatchFlagged
        });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// TX-P06: Archives the raw payload to S3 in a background task with its own DI scope,
    /// then updates the transaction row with the returned reference.
    /// Runs after the response is returned to the caller to avoid S3 latency on the hot path.
    /// </summary>
    private async Task ArchiveRawPayloadInBackgroundAsync(
        Guid legalEntityId,
        IngestTransactionCommand command,
        CanonicalTransaction canonical,
        Guid transactionId)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var archiver = scope.ServiceProvider.GetRequiredService<IRawPayloadArchiver>();
        var db = scope.ServiceProvider.GetRequiredService<IIngestDbContext>();
        try
        {
            var rawPayloadRef = await archiver.ArchiveAsync(
                legalEntityId.ToString(), command.SiteCode,
                canonical.FccTransactionId, command.RawPayload,
                CancellationToken.None);

            await db.SetRawPayloadRefAsync(transactionId, rawPayloadRef, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Background raw payload archiving failed for {FccTransactionId}; RawPayloadRef not set.",
                canonical.FccTransactionId);
        }
    }

    private async Task WriteDeduplicatedEventAsync(
        CanonicalTransaction canonical,
        Guid legalEntityId,
        Guid originalId,
        Guid correlationId,
        CancellationToken ct)
    {
        try
        {
            _db.AddOutboxMessage(new OutboxMessage
            {
                EventType = "TransactionDeduplicated",
                Payload = JsonSerializer.Serialize(new
                {
                    fccTransactionId = canonical.FccTransactionId,
                    existingTransactionId = originalId,
                    dedupKey = $"{canonical.FccTransactionId}:{canonical.SiteCode}",
                    siteCode = canonical.SiteCode,
                    legalEntityId
                }),
                CorrelationId = correlationId,
                CreatedAt = DateTimeOffset.UtcNow
            });

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write TransactionDeduplicated outbox event.");
        }
    }

    private static OutboxMessage BuildIngestedOutboxMessage(Transaction tx, Guid correlationId) =>
        new()
        {
            EventType = "TransactionIngested",
            Payload = JsonSerializer.Serialize(new
            {
                transactionId = tx.Id,
                fccTransactionId = tx.FccTransactionId,
                fccVendor = tx.FccVendor.ToString(),
                pumpNumber = tx.PumpNumber,
                totalAmountMinorUnits = tx.AmountMinorUnits,
                currencyCode = tx.CurrencyCode,
                siteCode = tx.SiteCode,
                legalEntityId = tx.LegalEntityId
            }),
            CorrelationId = correlationId,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("23505")                            // Npgsql SqlState
            || message.Contains("ix_transactions_dedup")
            || message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);
    }
}

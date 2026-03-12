using System.Text.Json;
using FccMiddleware.Application.Common;
using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Exceptions;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Models.Adapter;
using MediatR;
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
    private readonly IRawPayloadArchiver _rawPayloadArchiver;
    private readonly IIngestDbContext _db;
    private readonly ReconciliationMatchingService _reconciliationMatchingService;
    private readonly ILogger<IngestTransactionHandler> _logger;

    public IngestTransactionHandler(
        IFccAdapterFactory adapterFactory,
        ISiteFccConfigProvider siteFccConfigProvider,
        IDeduplicationService deduplicationService,
        IRawPayloadArchiver rawPayloadArchiver,
        IIngestDbContext db,
        ReconciliationMatchingService reconciliationMatchingService,
        ILogger<IngestTransactionHandler> logger)
    {
        _adapterFactory = adapterFactory;
        _siteFccConfigProvider = siteFccConfigProvider;
        _deduplicationService = deduplicationService;
        _rawPayloadArchiver = rawPayloadArchiver;
        _db = db;
        _reconciliationMatchingService = reconciliationMatchingService;
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

            return Result<IngestTransactionResult>.Failure(
                $"VALIDATION.{validation.ErrorCode}",
                validation.Message ?? "Payload validation failed.");
        }

        // ── Step 5: Normalize ─────────────────────────────────────────────────
        var canonical = adapter.NormalizeTransaction(envelope);

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
            IngestionSource = IngestionSource.FCC_PUSH,
            OdooOrderId = canonical.OdooOrderId,
            CorrelationId = command.CorrelationId,
            ReconciliationStatus = fuzzyMatchFlagged ? ReconciliationStatus.REVIEW_FUZZY_MATCH : null,
            SchemaVersion = 1
        };

        // ── Step 9: Archive raw payload to S3 (non-fatal on failure) ─────────
        try
        {
            transaction.RawPayloadRef = await _rawPayloadArchiver.ArchiveAsync(
                legalEntityId.ToString(), command.SiteCode,
                canonical.FccTransactionId, command.RawPayload,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Raw payload archiving failed for {FccTransactionId}; continuing without ref.",
                canonical.FccTransactionId);
        }

        // ── Step 10: Persist transaction + outbox event (single DB transaction) ──
        _db.AddTransaction(transaction);
        _db.AddOutboxMessage(BuildIngestedOutboxMessage(transaction, command.CorrelationId));

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

            return Result<IngestTransactionResult>.Success(new IngestTransactionResult
            {
                TransactionId = Guid.Empty,
                IsDuplicate = true,
                OriginalTransactionId = originalId
            });
        }

        await _reconciliationMatchingService.MatchAsync(transaction, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        // ── Step 11: Populate Redis cache after successful DB commit ──────────
        await _deduplicationService.SetCacheAsync(
            canonical.FccTransactionId, canonical.SiteCode,
            transaction.Id, DefaultDedupWindowDays, cancellationToken);

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

using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Buffer;

/// <summary>
/// Manages the local transaction buffer: insert with dedup, batch retrieval for upload,
/// status transitions, local API queries, and telemetry stats.
/// </summary>
public sealed class TransactionBufferManager
{
    private readonly AgentDbContext _db;
    private readonly ILogger<TransactionBufferManager> _logger;

    public TransactionBufferManager(AgentDbContext db, ILogger<TransactionBufferManager> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Buffers a canonical transaction locally with dedup on (FccTransactionId, SiteCode).
    /// Returns true if inserted, false if duplicate (silently ignored).
    /// </summary>
    public async Task<bool> BufferTransactionAsync(CanonicalTransaction tx, CancellationToken ct = default)
    {
        var entity = new BufferedTransaction
        {
            Id = tx.Id,
            FccTransactionId = tx.FccTransactionId,
            SiteCode = tx.SiteCode,
            PumpNumber = tx.PumpNumber,
            NozzleNumber = tx.NozzleNumber,
            ProductCode = tx.ProductCode,
            VolumeMicrolitres = tx.VolumeMicrolitres,
            AmountMinorUnits = tx.AmountMinorUnits,
            UnitPriceMinorPerLitre = tx.UnitPriceMinorPerLitre,
            CurrencyCode = tx.CurrencyCode,
            StartedAt = tx.StartedAt,
            CompletedAt = tx.CompletedAt,
            FiscalReceiptNumber = tx.FiscalReceiptNumber,
            FccVendor = tx.FccVendor,
            AttendantId = tx.AttendantId,
            Status = TransactionStatus.Pending,
            SyncStatus = SyncStatus.Pending,
            IngestionSource = tx.IngestionSource,
            RawPayloadJson = tx.RawPayloadJson ?? string.Empty,
            CorrelationId = tx.CorrelationId,
            SchemaVersion = tx.SchemaVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.Transactions.Add(entity);

        try
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogDebug("Buffered transaction {FccTransactionId} for site {SiteCode}",
                tx.FccTransactionId, tx.SiteCode);
            return true;
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            // Dedup: FCC poll may return the same transaction twice — skip silently
            _logger.LogDebug("Duplicate transaction {FccTransactionId} for site {SiteCode} skipped",
                tx.FccTransactionId, tx.SiteCode);
            _db.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>
    /// Returns the oldest Pending transactions for upload, ordered by CreatedAt ASC.
    /// Never skips past a failed record — retries oldest batch first.
    /// </summary>
    public async Task<IReadOnlyList<BufferedTransaction>> GetPendingBatchAsync(int batchSize, CancellationToken ct = default)
    {
        return await _db.Transactions
            .Where(t => t.SyncStatus == SyncStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .Take(batchSize)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>
    /// Marks transactions as Uploaded after successful cloud upload.
    /// </summary>
    public async Task<int> MarkUploadedAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        return await _db.Transactions
            .Where(t => ids.Contains(t.Id) && t.SyncStatus == SyncStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.SyncStatus, SyncStatus.Uploaded)
                // M-08: Update Status to Synced to reflect that the transaction
                // has been successfully uploaded to cloud.
                .SetProperty(t => t.Status, TransactionStatus.Synced)
                .SetProperty(t => t.UpdatedAt, now), ct);
    }

    /// <summary>
    /// Marks transactions as DuplicateConfirmed when cloud reports them as already-known duplicates.
    /// </summary>
    public async Task<int> MarkDuplicateConfirmedAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        return await _db.Transactions
            .Where(t => ids.Contains(t.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.SyncStatus, SyncStatus.DuplicateConfirmed)
                .SetProperty(t => t.Status, TransactionStatus.Duplicate)
                .SetProperty(t => t.UpdatedAt, now), ct);
    }

    /// <summary>
    /// Increments upload attempt counter and records the failure reason.
    /// Records remain Pending so they are retried on the next cadence tick.
    /// </summary>
    public async Task<int> RecordUploadFailureAsync(
        IReadOnlyList<string> ids,
        string error,
        CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        // Truncate error message to avoid storing excessively large strings.
        var truncatedError = error.Length > 500 ? error[..500] : error;

        return await _db.Transactions
            .Where(t => ids.Contains(t.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.UploadAttempts, t => t.UploadAttempts + 1)
                .SetProperty(t => t.LastUploadAttemptAt, now)
                .SetProperty(t => t.LastUploadError, truncatedError)
                .SetProperty(t => t.UpdatedAt, now), ct);
    }

    /// <summary>
    /// Marks transactions as SyncedToOdoo based on FCC transaction IDs from cloud status poll.
    /// </summary>
    public async Task<int> MarkSyncedToOdooAsync(IReadOnlyList<string> fccTransactionIds, CancellationToken ct = default)
    {
        if (fccTransactionIds.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        return await _db.Transactions
            .Where(t => fccTransactionIds.Contains(t.FccTransactionId) && t.SyncStatus == SyncStatus.Uploaded)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.SyncStatus, SyncStatus.SyncedToOdoo)
                .SetProperty(t => t.Status, TransactionStatus.SyncedToOdoo)
                .SetProperty(t => t.UpdatedAt, now), ct);
    }

    /// <summary>
    /// Returns transactions for the local API. Excludes SyncedToOdoo, DuplicateConfirmed, and Archived
    /// records to prevent Odoo double-consumption. Ordered by CompletedAt DESC.
    /// </summary>
    public async Task<IReadOnlyList<BufferedTransaction>> GetForLocalApiAsync(
        int? pumpNumber, int limit, int offset, CancellationToken ct = default)
    {
        var query = _db.Transactions
            .Where(t => t.SyncStatus == SyncStatus.Pending || t.SyncStatus == SyncStatus.Uploaded);

        if (pumpNumber.HasValue)
            query = query.Where(t => t.PumpNumber == pumpNumber.Value);

        return await query
            .OrderByDescending(t => t.CompletedAt)
            .Skip(offset)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns transaction counts grouped by SyncStatus for telemetry,
    /// plus the oldest pending record timestamp for sync lag calculation.
    /// </summary>
    public async Task<BufferStats> GetBufferStatsAsync(CancellationToken ct = default)
    {
        var counts = await _db.Transactions
            .GroupBy(t => t.SyncStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var oldestPending = await _db.Transactions
            .Where(t => t.SyncStatus == SyncStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .Select(t => (DateTimeOffset?)t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return new BufferStats
        {
            Pending = counts.FirstOrDefault(c => c.Status == SyncStatus.Pending)?.Count ?? 0,
            Uploaded = counts.FirstOrDefault(c => c.Status == SyncStatus.Uploaded)?.Count ?? 0,
            DuplicateConfirmed = counts.FirstOrDefault(c => c.Status == SyncStatus.DuplicateConfirmed)?.Count ?? 0,
            SyncedToOdoo = counts.FirstOrDefault(c => c.Status == SyncStatus.SyncedToOdoo)?.Count ?? 0,
            Archived = counts.FirstOrDefault(c => c.Status == SyncStatus.Archived)?.Count ?? 0,
            DeadLetter = counts.FirstOrDefault(c => c.Status == SyncStatus.DeadLetter)?.Count ?? 0,
            OldestPendingAtUtc = oldestPending,
        };
    }

    /// <summary>
    /// Cursor-based query for the local API. Returns transactions with CompletedAt before the cursor,
    /// ordered by CompletedAt DESC. Only returns Pending/Uploaded records (not yet synced to Odoo).
    /// </summary>
    public async Task<(IReadOnlyList<BufferedTransaction> Items, string? NextCursor)> GetPagedForLocalApiAsync(
        DateTimeOffset? cursor, int pageSize, string? status, int? pumpNumber,
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default)
    {
        var query = _db.Transactions
            .Where(t => t.SyncStatus == SyncStatus.Pending || t.SyncStatus == SyncStatus.Uploaded);

        if (cursor.HasValue)
            query = query.Where(t => t.CompletedAt < cursor.Value);

        if (pumpNumber.HasValue)
            query = query.Where(t => t.PumpNumber == pumpNumber.Value);

        if (from.HasValue)
            query = query.Where(t => t.CompletedAt >= from.Value);

        if (to.HasValue)
            query = query.Where(t => t.CompletedAt <= to.Value);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SyncStatus>(status, ignoreCase: true, out var parsedStatus))
            query = query.Where(t => t.SyncStatus == parsedStatus);

        // Fetch one extra to detect if there's a next page
        var items = await query
            .OrderByDescending(t => t.CompletedAt)
            .Take(pageSize + 1)
            .AsNoTracking()
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items.RemoveAt(items.Count - 1);
            nextCursor = items[^1].CompletedAt.ToString("O");
        }

        return (items, nextCursor);
    }

    /// <summary>
    /// Returns a single transaction by its middleware UUID, or null if not found.
    /// </summary>
    public async Task<BufferedTransaction?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        return await _db.Transactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    /// <summary>
    /// Stamps an Odoo order ID on a transaction. Idempotent: same odooOrderId returns success.
    /// Different odooOrderId on an already-acknowledged transaction returns false (conflict).
    /// </summary>
    public async Task<AcknowledgeResult> AcknowledgeAsync(
        string transactionId, string odooOrderId, CancellationToken ct = default)
    {
        var tx = await _db.Transactions.FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (tx is null)
            return AcknowledgeResult.NotFound;

        if (tx.OdooOrderId is not null)
        {
            // Idempotent: same order ID → success
            if (string.Equals(tx.OdooOrderId, odooOrderId, StringComparison.Ordinal))
                return AcknowledgeResult.Success;

            // Conflict: different order ID already stamped
            return AcknowledgeResult.Conflict;
        }

        tx.OdooOrderId = odooOrderId;
        tx.AcknowledgedAt = DateTimeOffset.UtcNow;
        tx.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Transaction {Id} acknowledged with OdooOrderId {OdooOrderId}",
            transactionId, odooOrderId);
        return AcknowledgeResult.Success;
    }

    /// <summary>Maximum upload attempts before a record is dead-lettered (GAP-1).</summary>
    public const int MaxUploadAttempts = 20;

    /// <summary>
    /// GAP-1: Transitions Pending records that have exhausted upload retries to DeadLetter.
    /// Dead-lettered records are excluded from upload batches but remain queryable for diagnostics.
    /// </summary>
    public async Task<int> DeadLetterExhaustedAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var count = await _db.Transactions
            .Where(t => t.SyncStatus == SyncStatus.Pending && t.UploadAttempts >= MaxUploadAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.SyncStatus, SyncStatus.DeadLetter)
                // M-08: Update Status to StalePending so the lifecycle status reflects
                // that this transaction failed to sync and requires manual intervention.
                .SetProperty(t => t.Status, TransactionStatus.StalePending)
                .SetProperty(t => t.UpdatedAt, now), ct);

        if (count > 0)
            _logger.LogWarning("Dead-lettered {Count} records that exceeded {Max} upload attempts", count, MaxUploadAttempts);

        return count;
    }

    /// <summary>
    /// GAP-2: Reverts Uploaded records older than <paramref name="staleDays"/> back to Pending for re-upload.
    /// Handles the case where cloud accepted the upload but the Odoo sync poll never confirmed it.
    /// Re-uploading is safe because the cloud deduplicates by FccTransactionId.
    /// </summary>
    public async Task<int> RevertStaleUploadedAsync(int staleDays = 3, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-staleDays);
        var now = DateTimeOffset.UtcNow;

        var count = await _db.Transactions
            .Where(t => t.SyncStatus == SyncStatus.Uploaded && t.UpdatedAt < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.SyncStatus, SyncStatus.Pending)
                // M-08: Reset Status back to Pending to keep it in sync with SyncStatus.
                .SetProperty(t => t.Status, TransactionStatus.Pending)
                .SetProperty(t => t.UploadAttempts, 0)
                .SetProperty(t => t.UpdatedAt, now), ct);

        if (count > 0)
            _logger.LogWarning("Reverted {Count} stale Uploaded records (older than {Days}d) back to Pending", count, staleDays);

        return count;
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        // SQLite unique constraint violation message
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Result of an acknowledge operation.</summary>
public enum AcknowledgeResult
{
    Success,
    NotFound,
    Conflict,
}

/// <summary>Transaction buffer counts by SyncStatus for telemetry.</summary>
public sealed class BufferStats
{
    public int Pending { get; init; }
    public int Uploaded { get; init; }
    public int DuplicateConfirmed { get; init; }
    public int SyncedToOdoo { get; init; }
    public int Archived { get; init; }
    public int DeadLetter { get; init; }
    public int Total => Pending + Uploaded + DuplicateConfirmed + SyncedToOdoo + Archived + DeadLetter;

    /// <summary>CreatedAt of the oldest Pending record. Null if no pending records.</summary>
    public DateTimeOffset? OldestPendingAtUtc { get; init; }
}

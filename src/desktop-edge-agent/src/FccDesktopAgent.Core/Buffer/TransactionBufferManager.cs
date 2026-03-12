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
            OldestPendingAtUtc = oldestPending,
        };
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        // SQLite unique constraint violation message
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Transaction buffer counts by SyncStatus for telemetry.</summary>
public sealed class BufferStats
{
    public int Pending { get; init; }
    public int Uploaded { get; init; }
    public int DuplicateConfirmed { get; init; }
    public int SyncedToOdoo { get; init; }
    public int Archived { get; init; }
    public int Total => Pending + Uploaded + DuplicateConfirmed + SyncedToOdoo + Archived;

    /// <summary>CreatedAt of the oldest Pending record. Null if no pending records.</summary>
    public DateTimeOffset? OldestPendingAtUtc { get; init; }
}

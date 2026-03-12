using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Ingestion;

/// <summary>
/// Polls the FCC for new transactions and buffers them locally.
/// Invoked on each cadence tick by <see cref="Runtime.CadenceController"/>.
///
/// Architecture rules satisfied:
/// - Rule #2: No transaction left behind — every polled transaction is buffered before cursor advance.
/// - Rule #8: FCC transaction IDs preserved as opaque strings.
/// - Rule #10: No independent timer loop — scheduling entirely owned by the cadence controller.
/// </summary>
public sealed class IngestionOrchestrator : IIngestionOrchestrator
{
    private readonly IFccAdapterFactory _adapterFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly ILogger<IngestionOrchestrator> _logger;

    public IngestionOrchestrator(
        IFccAdapterFactory adapterFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<AgentConfiguration> config,
        ILogger<IngestionOrchestrator> logger)
    {
        _adapterFactory = adapterFactory;
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IngestionResult> PollAndBufferAsync(CancellationToken ct)
    {
        var config = _config.Value;

        // Create a scope per poll cycle — TransactionBufferManager and AgentDbContext are scoped.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var bufferManager = scope.ServiceProvider.GetRequiredService<TransactionBufferManager>();

        var connectionConfig = new FccConnectionConfig(
            BaseUrl: config.FccBaseUrl,
            ApiKey: config.FccApiKey,
            RequestTimeout: TimeSpan.FromSeconds(10),
            SiteCode: config.SiteId);

        var adapter = _adapterFactory.Create(config.FccVendor, connectionConfig);

        // Load cursor from single-row sync_state sentinel (Id = 1).
        var syncState = await db.SyncStates
            .FirstOrDefaultAsync(s => s.Id == 1, ct);

        bool isNewSyncState = syncState is null;
        syncState ??= new SyncStateRecord { Id = 1 };

        var cursor = new FetchCursor(
            LastSequence: syncState.LastFccSequence,
            Since: null,
            MaxCount: 50);

        int newCount = 0;
        int dupCount = 0;
        string? advancedSequence = syncState.LastFccSequence;

        // Fetch-and-buffer loop. Architecture rule #2: buffer ALL records before advancing cursor.
        while (!ct.IsCancellationRequested)
        {
            TransactionBatch batch;
            try
            {
                batch = await adapter.FetchTransactionsAsync(cursor, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "FetchTransactionsAsync failed at cursor={Cursor}; aborting poll cycle",
                    cursor.LastSequence);
                break;
            }

            if (batch.Records.Count == 0)
                break;

            foreach (var raw in batch.Records)
            {
                ct.ThrowIfCancellationRequested();

                CanonicalTransaction tx;
                try
                {
                    tx = await adapter.NormalizeAsync(raw, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "NormalizeAsync failed for vendor={Vendor} site={Site}; skipping record",
                        raw.FccVendor, raw.SiteCode);
                    continue;
                }

                var inserted = await bufferManager.BufferTransactionAsync(tx, ct);
                if (inserted)
                    newCount++;
                else
                    dupCount++;
            }

            // Advance cursor after successfully buffering the entire batch.
            if (batch.NextCursor is not null)
                advancedSequence = batch.NextCursor;

            // Stop if no more pages or no next cursor to advance to (defensive against infinite loops).
            if (!batch.HasMore || batch.NextCursor is null)
                break;

            cursor = cursor with { LastSequence = batch.NextCursor };
        }

        // Persist cursor only if it advanced.
        if (advancedSequence != syncState.LastFccSequence)
        {
            syncState.LastFccSequence = advancedSequence;
            syncState.UpdatedAt = DateTimeOffset.UtcNow;

            if (isNewSyncState)
                db.SyncStates.Add(syncState);
            else
                db.SyncStates.Update(syncState);

            await db.SaveChangesAsync(ct);

            _logger.LogDebug("FCC cursor advanced to {Cursor}", advancedSequence);
        }

        return new IngestionResult(newCount, dupCount, advancedSequence);
    }
}

using FccDesktopAgent.Core.Adapter.Advatec;
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
/// Invoked on each cadence tick by <see cref="Runtime.CadenceController"/>
/// and on-demand via <see cref="ManualPullAsync"/> (DEA-2.7).
///
/// Architecture rules satisfied:
/// - Rule #2: No transaction left behind — every polled transaction is buffered before cursor advance.
/// - Rule #8: FCC transaction IDs preserved as opaque strings.
/// - Rule #10: No independent timer loop — scheduling entirely owned by the cadence controller.
///
/// DEA-2.7: A single <see cref="SemaphoreSlim"/> serializes scheduled and manual pulls so
/// they never race on cursor state or the buffer.
/// </summary>
public sealed class IngestionOrchestrator : IIngestionOrchestrator
{
    private const int MaxFiscalAttempts = 5;
    private const long FiscalBaseBackoffMs = 30_000;
    private const long FiscalMaxBackoffMs = 480_000;

    private readonly IFccAdapterFactory _adapterFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly IConfigManager? _configManager;
    private readonly ILogger<IngestionOrchestrator> _logger;
    private readonly ILoggerFactory? _loggerFactory;

    // Serializes scheduled (CadenceController) and manual (API) poll cycles.
    // SemaphoreSlim(1,1) = mutual exclusion; WaitAsync accepts CancellationToken so shutdown is clean.
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    // ── Fiscalization (ADV-7.3) ───────────────────────────────────────────────
    // Lazy-created when site config indicates fiscalizationMode = FCC_DIRECT + vendor = ADVATEC.
    // Cached and reused across poll cycles; recreated when config fingerprint changes.

    private AdvatecFiscalizationService? _fiscalizationService;
    private string? _fiscalizationConfigFingerprint;

    public IngestionOrchestrator(
        IFccAdapterFactory adapterFactory,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentConfiguration> config,
        ILogger<IngestionOrchestrator> logger,
        IConfigManager? configManager = null,
        ILoggerFactory? loggerFactory = null)
    {
        _adapterFactory = adapterFactory;
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
        _configManager = configManager;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public async Task<IngestionResult> PollAndBufferAsync(CancellationToken ct)
    {
        await _pollLock.WaitAsync(ct);
        try
        {
            return await DoPollAndBufferAsync(ct);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IngestionResult> ManualPullAsync(int? pumpNumber, CancellationToken ct)
    {
        _logger.LogInformation(
            "Manual FCC pull requested (pumpNumber={PumpNumber})", pumpNumber);

        await _pollLock.WaitAsync(ct);
        try
        {
            return await DoPollAndBufferAsync(ct);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task EnsurePushListenersInitializedAsync(CancellationToken ct)
    {
        var config = _config.CurrentValue;

        ResolvedFccRuntimeConfiguration resolvedConfig;
        try
        {
            resolvedConfig = DesktopFccRuntimeConfiguration.Resolve(
                config,
                _configManager?.CurrentSiteConfig,
                TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Push listener init skipped: runtime configuration not ready");
            return;
        }

        // Petronite and Advatec are push-mode — other vendors use polling and don't need early init.
        if (resolvedConfig.Vendor != FccVendor.Petronite && resolvedConfig.Vendor != FccVendor.Advatec)
            return;

        _logger.LogInformation("Initializing push-mode listener for {Vendor}", resolvedConfig.Vendor);

        await _pollLock.WaitAsync(ct);
        try
        {
            var adapter = _adapterFactory.Create(resolvedConfig.Vendor, resolvedConfig.ConnectionConfig);

            // Trigger the lazy init (webhook listener start + startup reconciliation)
            // by calling FetchTransactionsAsync with a minimal cursor. This is idempotent.
            await adapter.FetchTransactionsAsync(new FetchCursor(null, null, 0), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Push listener initialization failed for {Vendor} (non-fatal — will retry on next poll)",
                resolvedConfig.Vendor);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    // ── Core fetch-and-buffer loop ────────────────────────────────────────────

    private async Task<IngestionResult> DoPollAndBufferAsync(CancellationToken ct)
    {
        var config = _config.CurrentValue;
        var siteConfig = _configManager?.CurrentSiteConfig;

        // Create a scope per poll cycle — TransactionBufferManager and AgentDbContext are scoped.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var bufferManager = scope.ServiceProvider.GetRequiredService<TransactionBufferManager>();

        ResolvedFccRuntimeConfiguration resolvedConfig;
        IFccAdapter adapter;
        try
        {
            resolvedConfig = DesktopFccRuntimeConfiguration.Resolve(
                config,
                siteConfig,
                TimeSpan.FromSeconds(10));
            adapter = _adapterFactory.Create(resolvedConfig.Vendor, resolvedConfig.ConnectionConfig);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            _logger.LogError(ex, "FCC polling skipped: runtime configuration is invalid");
            return new IngestionResult(0, 0, null);
        }

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
        int fetchCycles = 0;
        string? advancedSequence = syncState.LastFccSequence;

        // ADV-7.3: Collect newly buffered transactions for post-dispense fiscalization.
        var fiscalizationService = ResolveFiscalizationService(siteConfig, resolvedConfig.ConnectionConfig);
        List<CanonicalTransaction>? txsToFiscalize = fiscalizationService is not null
            ? new List<CanonicalTransaction>()
            : null;

        // Fetch-and-buffer loop. Architecture rule #2: buffer ALL records before advancing cursor.
        while (!ct.IsCancellationRequested)
        {
            TransactionBatch batch;
            try
            {
                batch = await adapter.FetchTransactionsAsync(cursor, ct);
                fetchCycles++;
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
                {
                    newCount++;

                    // ADV-7.3: Queue for fiscalization if the transaction doesn't already have a fiscal receipt.
                    if (txsToFiscalize is not null && string.IsNullOrEmpty(tx.FiscalReceiptNumber))
                        txsToFiscalize.Add(tx);
                }
                else
                {
                    dupCount++;
                }
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

        // ── ADV-7.3 / GAP-7: Mark newly buffered transactions for fiscalization ─
        if (txsToFiscalize is { Count: > 0 })
        {
            foreach (var tx in txsToFiscalize)
            {
                var btx = await db.Transactions
                    .FirstOrDefaultAsync(t => t.Id == tx.Id && t.FiscalStatus == "NONE", ct);
                if (btx is not null)
                {
                    btx.FiscalStatus = "PENDING";
                    btx.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            await db.SaveChangesAsync(ct);

            // Attempt immediate retry for newly queued transactions
            await RetryPendingFiscalizationAsync(db, fiscalizationService, siteConfig, ct);
        }

        return new IngestionResult(newCount, dupCount, advancedSequence, fetchCycles);
    }

    /// <summary>
    /// Retries pending fiscalization with exponential backoff (GAP-7).
    /// Dead-letters transactions after MaxFiscalAttempts failures.
    /// </summary>
    private async Task RetryPendingFiscalizationAsync(
        AgentDbContext db,
        IFiscalizationService? fiscalizationService,
        SiteConfig? siteConfig,
        CancellationToken ct)
    {
        if (fiscalizationService is null) return;

        var backoffThreshold = DateTimeOffset.UtcNow.AddMilliseconds(-FiscalBaseBackoffMs);

        var pending = await db.Transactions
            .Where(t => t.FiscalStatus == "PENDING"
                && t.FiscalAttempts < MaxFiscalAttempts
                && (t.LastFiscalAttemptAt == null || t.LastFiscalAttemptAt < backoffThreshold))
            .OrderBy(t => t.CreatedAt)
            .Take(10)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        _logger.LogInformation("Fiscalization retry: {Count} transaction(s) pending", pending.Count);

        foreach (var btx in pending)
        {
            if (ct.IsCancellationRequested) break;

            // Per-record exponential backoff check
            var requiredBackoffMs = Math.Min(
                FiscalBaseBackoffMs * (1L << Math.Min(btx.FiscalAttempts, 10)),
                FiscalMaxBackoffMs);

            if (btx.LastFiscalAttemptAt.HasValue)
            {
                var elapsed = (DateTimeOffset.UtcNow - btx.LastFiscalAttemptAt.Value).TotalMilliseconds;
                if (elapsed < requiredBackoffMs) continue;
            }

            try
            {
                var tx = new CanonicalTransaction
                {
                    Id = btx.Id,
                    FccTransactionId = btx.FccTransactionId,
                    SiteCode = btx.SiteCode,
                    PumpNumber = btx.PumpNumber,
                    NozzleNumber = btx.NozzleNumber,
                    ProductCode = btx.ProductCode,
                    VolumeMicrolitres = btx.VolumeMicrolitres,
                    AmountMinorUnits = btx.AmountMinorUnits,
                    UnitPriceMinorPerLitre = btx.UnitPriceMinorPerLitre,
                    CurrencyCode = btx.CurrencyCode,
                    StartedAt = btx.StartedAt,
                    CompletedAt = btx.CompletedAt,
                    FccVendor = btx.FccVendor,
                };

                var context = BuildFiscalizationContext(siteConfig);
                var result = await fiscalizationService.SubmitForFiscalizationAsync(tx, context, ct);

                if (result.Success && !string.IsNullOrEmpty(result.ReceiptCode))
                {
                    btx.FiscalStatus = "SUCCESS";
                    btx.FiscalReceiptNumber = result.ReceiptCode;
                    btx.FiscalAttempts++;
                    btx.LastFiscalAttemptAt = DateTimeOffset.UtcNow;
                    btx.UpdatedAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation(
                        "Fiscalization retry: receipt attached to tx {TxId} — attempt={Attempt}",
                        btx.Id, btx.FiscalAttempts);
                }
                else
                {
                    btx.FiscalAttempts++;
                    btx.LastFiscalAttemptAt = DateTimeOffset.UtcNow;
                    btx.UpdatedAt = DateTimeOffset.UtcNow;

                    if (btx.FiscalAttempts >= MaxFiscalAttempts)
                    {
                        btx.FiscalStatus = "DEAD_LETTER";
                        _logger.LogError(
                            "Fiscalization dead-letter: tx {TxId} exceeded {Max} attempts — {Error}",
                            btx.Id, MaxFiscalAttempts, result.ErrorMessage);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Fiscalization retry failed: tx {TxId} attempt={Attempt} — {Error}",
                            btx.Id, btx.FiscalAttempts, result.ErrorMessage);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                btx.FiscalAttempts++;
                btx.LastFiscalAttemptAt = DateTimeOffset.UtcNow;
                btx.UpdatedAt = DateTimeOffset.UtcNow;

                if (btx.FiscalAttempts >= MaxFiscalAttempts)
                {
                    btx.FiscalStatus = "DEAD_LETTER";
                    _logger.LogError(ex,
                        "Fiscalization dead-letter: tx {TxId} exceeded {Max} attempts",
                        btx.Id, MaxFiscalAttempts);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "Fiscalization retry error: tx {TxId} attempt={Attempt}",
                        btx.Id, btx.FiscalAttempts);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Fiscalization helpers (ADV-7.3) ───────────────────────────────────────

    /// <summary>
    /// Resolves the fiscalization service based on site config.
    /// Returns null if fiscalization is not configured or the vendor is not Advatec.
    /// Caches the service and recreates it when the config fingerprint changes.
    /// </summary>
    private IFiscalizationService? ResolveFiscalizationService(
        SiteConfig? siteConfig,
        FccConnectionConfig connectionConfig)
    {
        // Only activate for FCC_DIRECT mode with Advatec vendor
        if (siteConfig?.Fiscalization?.Mode is not "FCC_DIRECT"
            || !string.Equals(siteConfig.Fiscalization.Vendor, "ADVATEC", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (_loggerFactory is null)
        {
            _logger.LogDebug("Fiscalization: ILoggerFactory not available — skipping");
            return null;
        }

        // Check if existing service matches current config
        var fingerprint = $"{connectionConfig.AdvatecDeviceAddress}|{connectionConfig.AdvatecDevicePort}"
            + $"|{connectionConfig.AdvatecWebhookToken}|{connectionConfig.AdvatecWebhookListenerPort}";

        if (_fiscalizationService is not null && _fiscalizationConfigFingerprint == fingerprint)
            return _fiscalizationService;

        // Dispose old instance
        if (_fiscalizationService is not null)
        {
            _logger.LogInformation("Fiscalization: config changed — recreating service");
            _fiscalizationService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _fiscalizationService = new AdvatecFiscalizationService(connectionConfig, _loggerFactory);
        _fiscalizationConfigFingerprint = fingerprint;
        return _fiscalizationService;
    }

    /// <summary>
    /// Builds a default fiscalization context from site config.
    /// In a full implementation, this would be populated from the pre-auth record
    /// or customer data associated with the transaction.
    /// </summary>
    private static FiscalizationContext BuildFiscalizationContext(SiteConfig? siteConfig)
    {
        return new FiscalizationContext(
            CustomerTaxId: null,
            CustomerName: null,
            CustomerIdType: null,
            PaymentType: "CASH");
    }
}

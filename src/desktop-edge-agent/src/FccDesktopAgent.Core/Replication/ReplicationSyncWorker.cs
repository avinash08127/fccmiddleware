using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Peer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Replication;

/// <summary>
/// Pulls replication data from the primary and applies it to the local database.
/// </summary>
public interface IReplicationSyncWorker
{
    /// <summary>
    /// Execute one replication sync cycle: bootstrap (if needed) or delta sync.
    /// </summary>
    Task SyncAsync(CancellationToken ct);
}

public sealed class ReplicationSyncWorker : IReplicationSyncWorker
{
    private readonly IPeerHttpClient _peerClient;
    private readonly IPeerCoordinator _coordinator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly ILogger<ReplicationSyncWorker> _logger;

    /// <summary>Default delta sync batch size.</summary>
    private const int DeltaBatchSize = 200;

    public ReplicationSyncWorker(
        IPeerHttpClient peerClient,
        IPeerCoordinator coordinator,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentConfiguration> config,
        ILogger<ReplicationSyncWorker> logger)
    {
        _peerClient = peerClient;
        _coordinator = coordinator;
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    public async Task SyncAsync(CancellationToken ct)
    {
        var cfg = _config.CurrentValue;
        if (!cfg.SiteHaEnabled || !cfg.ReplicationEnabled)
            return;

        if (cfg.CurrentRole == "PRIMARY")
            return; // Primary does not replicate from peers

        // Find the primary peer
        var primaryBaseUrl = FindPrimaryBaseUrl();
        if (primaryBaseUrl is null)
        {
            _logger.LogDebug("No primary peer found — skipping replication cycle");
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        // Load or create replication state
        var replState = await db.ReplicationStates.FindAsync(new object[] { 1 }, ct);
        if (replState is null)
        {
            replState = new ReplicationStateRecord { Id = 1 };
            db.ReplicationStates.Add(replState);
            await db.SaveChangesAsync(ct);
        }

        if (!replState.SnapshotComplete)
        {
            await BootstrapFromPrimaryAsync(db, replState, primaryBaseUrl, ct);
        }
        else
        {
            await DeltaSyncFromPrimaryAsync(db, replState, primaryBaseUrl, ct);
        }
    }

    private async Task BootstrapFromPrimaryAsync(
        AgentDbContext db, ReplicationStateRecord replState, string primaryBaseUrl, CancellationToken ct)
    {
        _logger.LogInformation("Starting bootstrap replication from primary at {Url}", primaryBaseUrl);

        var snapshot = await _peerClient.GetBootstrapAsync(primaryBaseUrl, ct);
        if (snapshot is null)
        {
            _logger.LogWarning("Bootstrap snapshot request returned null");
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Apply transaction records
            foreach (var tx in snapshot.Transactions)
            {
                var existing = await db.Transactions.FindAsync(new object[] { tx.Id }, ct);
                if (existing is null)
                {
                    db.Transactions.Add(MapToBufferedTransaction(tx));
                }
            }

            // Apply pre-auth records
            foreach (var pa in snapshot.PreAuths)
            {
                var existing = await db.PreAuths.FindAsync(new object[] { pa.Id }, ct);
                if (existing is null)
                {
                    db.PreAuths.Add(MapToPreAuthRecord(pa));
                }
            }

            // Update replication state
            replState.SnapshotComplete = true;
            replState.PrimaryAgentId = snapshot.PrimaryAgentId;
            replState.PrimaryEpoch = snapshot.Epoch;
            replState.LastAppliedTxSeq = snapshot.HighWaterMarkTxSeq;
            replState.LastAppliedPreAuthSeq = snapshot.HighWaterMarkPaSeq;
            replState.LastSnapshotAt = DateTimeOffset.UtcNow;
            replState.LastDeltaSyncAt = DateTimeOffset.UtcNow;
            replState.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            _logger.LogInformation(
                "Bootstrap complete: {TxCount} transactions, {PaCount} pre-auths from {PrimaryId}",
                snapshot.Transactions.Count, snapshot.PreAuths.Count, snapshot.PrimaryAgentId);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private async Task DeltaSyncFromPrimaryAsync(
        AgentDbContext db, ReplicationStateRecord replState, string primaryBaseUrl, CancellationToken ct)
    {
        var sinceSeq = Math.Max(replState.LastAppliedTxSeq, replState.LastAppliedPreAuthSeq);

        var delta = await _peerClient.GetDeltaSyncAsync(primaryBaseUrl, sinceSeq, DeltaBatchSize, ct);
        if (delta is null)
        {
            _logger.LogDebug("Delta sync request returned null");
            return;
        }

        if (delta.Transactions.Count == 0 && delta.PreAuths.Count == 0)
        {
            // Nothing new — update last sync time
            replState.LastDeltaSyncAt = DateTimeOffset.UtcNow;
            replState.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var tx in delta.Transactions)
            {
                var existing = await db.Transactions.FindAsync(new object[] { tx.Id }, ct);
                if (existing is null)
                {
                    db.Transactions.Add(MapToBufferedTransaction(tx));
                }
                else
                {
                    // Update existing record with newer data
                    existing.ReplicationSeq = tx.ReplicationSeq;
                    existing.SourceAgentId = tx.SourceAgentId;
                    existing.ReplicatedAt = DateTimeOffset.UtcNow;
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            foreach (var pa in delta.PreAuths)
            {
                var existing = await db.PreAuths.FindAsync(new object[] { pa.Id }, ct);
                if (existing is null)
                {
                    db.PreAuths.Add(MapToPreAuthRecord(pa));
                }
                else
                {
                    existing.ReplicationSeq = pa.ReplicationSeq;
                    existing.SourceAgentId = pa.SourceAgentId;
                    existing.ReplicatedAt = DateTimeOffset.UtcNow;
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            // Update replication cursors
            if (delta.Transactions.Count > 0)
                replState.LastAppliedTxSeq = delta.Transactions[^1].ReplicationSeq;
            if (delta.PreAuths.Count > 0)
                replState.LastAppliedPreAuthSeq = delta.PreAuths[^1].ReplicationSeq;
            replState.PrimaryEpoch = delta.Epoch;
            replState.LastDeltaSyncAt = DateTimeOffset.UtcNow;
            replState.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            _logger.LogDebug(
                "Delta sync applied: {TxCount} transactions, {PaCount} pre-auths (seq {From}→{To})",
                delta.Transactions.Count, delta.PreAuths.Count, delta.FromSeq, delta.ToSeq);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private string? FindPrimaryBaseUrl()
    {
        foreach (var (_, peer) in _coordinator.GetPeerStates())
        {
            if (peer.CurrentRole == "PRIMARY" &&
                peer.SuspectStatus == SuspectStatus.Healthy &&
                !string.IsNullOrEmpty(peer.PeerApiBaseUrl))
            {
                return peer.PeerApiBaseUrl;
            }
        }

        return null;
    }

    private static BufferedTransaction MapToBufferedTransaction(ReplicatedTransaction tx) => new()
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
        StartedAt = DateTimeOffset.Parse(tx.StartedAt),
        CompletedAt = DateTimeOffset.Parse(tx.CompletedAt),
        FiscalReceiptNumber = tx.FiscalReceiptNumber,
        FccVendor = tx.FccVendor,
        AttendantId = tx.AttendantId,
        IngestionSource = tx.IngestionSource,
        CorrelationId = tx.CorrelationId,
        PreAuthId = tx.PreAuthId,
        ReplicationSeq = tx.ReplicationSeq,
        SourceAgentId = tx.SourceAgentId,
        ReplicatedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.Parse(tx.CreatedAt),
        UpdatedAt = DateTimeOffset.Parse(tx.UpdatedAt),
    };

    private static PreAuthRecord MapToPreAuthRecord(ReplicatedPreAuth pa) => new()
    {
        Id = pa.Id,
        SiteCode = pa.SiteCode,
        OdooOrderId = pa.OdooOrderId,
        PumpNumber = pa.PumpNumber,
        NozzleNumber = pa.NozzleNumber,
        ProductCode = pa.ProductCode,
        RequestedAmount = pa.RequestedAmount,
        UnitPrice = pa.UnitPrice,
        Currency = pa.Currency,
        RequestedAt = DateTimeOffset.Parse(pa.RequestedAt),
        ExpiresAt = DateTimeOffset.Parse(pa.ExpiresAt),
        FccCorrelationId = pa.FccCorrelationId,
        FccAuthorizationCode = pa.FccAuthorizationCode,
        ReplicationSeq = pa.ReplicationSeq,
        SourceAgentId = pa.SourceAgentId,
        ReplicatedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.Parse(pa.CreatedAt),
        UpdatedAt = DateTimeOffset.Parse(pa.UpdatedAt),
    };
}

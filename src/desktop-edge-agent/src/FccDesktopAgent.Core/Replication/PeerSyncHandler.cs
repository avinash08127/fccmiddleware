using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Replication;

/// <summary>
/// Generates snapshot and delta payloads from the local database for peer replication.
/// Uses <see cref="IServiceScopeFactory"/> for scoped <see cref="AgentDbContext"/> access.
/// </summary>
public sealed class PeerSyncHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly ReplicationSequenceAssignor _sequenceAssignor;
    private readonly ILogger<PeerSyncHandler> _logger;

    public PeerSyncHandler(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentConfiguration> config,
        ReplicationSequenceAssignor sequenceAssignor,
        ILogger<PeerSyncHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _sequenceAssignor = sequenceAssignor;
        _logger = logger;
    }

    /// <summary>
    /// Generates a full snapshot of all active records for initial standby bootstrap.
    /// </summary>
    public async Task<SnapshotPayload> GenerateSnapshotAsync(CancellationToken ct)
    {
        var cfg = _config.CurrentValue;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var transactions = await db.Transactions
            .AsNoTracking()
            .OrderBy(t => t.ReplicationSeq)
            .Select(t => new ReplicatedTransaction
            {
                Id = t.Id,
                FccTransactionId = t.FccTransactionId,
                SiteCode = t.SiteCode,
                PumpNumber = t.PumpNumber,
                NozzleNumber = t.NozzleNumber,
                ProductCode = t.ProductCode,
                VolumeMicrolitres = t.VolumeMicrolitres,
                AmountMinorUnits = t.AmountMinorUnits,
                UnitPriceMinorPerLitre = t.UnitPriceMinorPerLitre,
                CurrencyCode = t.CurrencyCode,
                StartedAt = t.StartedAt.ToString("O"),
                CompletedAt = t.CompletedAt.ToString("O"),
                FiscalReceiptNumber = t.FiscalReceiptNumber,
                FccVendor = t.FccVendor,
                AttendantId = t.AttendantId,
                Status = t.Status.ToString(),
                SyncStatus = t.SyncStatus.ToString(),
                IngestionSource = t.IngestionSource,
                CorrelationId = t.CorrelationId,
                PreAuthId = t.PreAuthId,
                ReplicationSeq = t.ReplicationSeq,
                SourceAgentId = t.SourceAgentId ?? cfg.DeviceId,
                CreatedAt = t.CreatedAt.ToString("O"),
                UpdatedAt = t.UpdatedAt.ToString("O"),
            })
            .ToListAsync(ct);

        var preAuths = await db.PreAuths
            .AsNoTracking()
            .OrderBy(p => p.ReplicationSeq)
            .Select(p => new ReplicatedPreAuth
            {
                Id = p.Id,
                SiteCode = p.SiteCode,
                OdooOrderId = p.OdooOrderId,
                PumpNumber = p.PumpNumber,
                NozzleNumber = p.NozzleNumber,
                ProductCode = p.ProductCode,
                RequestedAmount = p.RequestedAmount,
                UnitPrice = p.UnitPrice,
                Currency = p.Currency,
                Status = p.Status.ToString(),
                RequestedAt = p.RequestedAt.ToString("O"),
                ExpiresAt = p.ExpiresAt.ToString("O"),
                FccCorrelationId = p.FccCorrelationId,
                FccAuthorizationCode = p.FccAuthorizationCode,
                ReplicationSeq = p.ReplicationSeq,
                SourceAgentId = p.SourceAgentId ?? cfg.DeviceId,
                CreatedAt = p.CreatedAt.ToString("O"),
                UpdatedAt = p.UpdatedAt.ToString("O"),
            })
            .ToListAsync(ct);

        var nozzles = await db.NozzleMappings
            .AsNoTracking()
            .Select(n => new ReplicatedNozzle
            {
                Id = n.Id,
                SiteCode = n.SiteCode,
                FccPumpNumber = n.FccPumpNumber,
                FccNozzleNumber = n.FccNozzleNumber,
                OdooPumpNumber = n.OdooPumpNumber,
                OdooNozzleNumber = n.OdooNozzleNumber,
                ProductCode = n.ProductCode,
            })
            .ToListAsync(ct);

        var highTxSeq = transactions.Count > 0 ? transactions[^1].ReplicationSeq : 0;
        var highPaSeq = preAuths.Count > 0 ? preAuths[^1].ReplicationSeq : 0;

        _logger.LogInformation(
            "Snapshot generated: {TxCount} transactions, {PaCount} pre-auths, {NozzleCount} nozzles",
            transactions.Count, preAuths.Count, nozzles.Count);

        return new SnapshotPayload
        {
            PrimaryAgentId = cfg.DeviceId,
            Epoch = _sequenceAssignor.CurrentSequence,
            HighWaterMarkTxSeq = highTxSeq,
            HighWaterMarkPaSeq = highPaSeq,
            Transactions = transactions,
            PreAuths = preAuths,
            Nozzles = nozzles,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Generates a delta payload of records changed since the given sequence.
    /// </summary>
    public async Task<DeltaSyncPayload> GenerateDeltaAsync(long sinceSeq, int limit, CancellationToken ct)
    {
        var cfg = _config.CurrentValue;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var transactions = await db.Transactions
            .AsNoTracking()
            .Where(t => t.ReplicationSeq > sinceSeq)
            .OrderBy(t => t.ReplicationSeq)
            .Take(limit)
            .Select(t => new ReplicatedTransaction
            {
                Id = t.Id,
                FccTransactionId = t.FccTransactionId,
                SiteCode = t.SiteCode,
                PumpNumber = t.PumpNumber,
                NozzleNumber = t.NozzleNumber,
                ProductCode = t.ProductCode,
                VolumeMicrolitres = t.VolumeMicrolitres,
                AmountMinorUnits = t.AmountMinorUnits,
                UnitPriceMinorPerLitre = t.UnitPriceMinorPerLitre,
                CurrencyCode = t.CurrencyCode,
                StartedAt = t.StartedAt.ToString("O"),
                CompletedAt = t.CompletedAt.ToString("O"),
                FiscalReceiptNumber = t.FiscalReceiptNumber,
                FccVendor = t.FccVendor,
                AttendantId = t.AttendantId,
                Status = t.Status.ToString(),
                SyncStatus = t.SyncStatus.ToString(),
                IngestionSource = t.IngestionSource,
                CorrelationId = t.CorrelationId,
                PreAuthId = t.PreAuthId,
                ReplicationSeq = t.ReplicationSeq,
                SourceAgentId = t.SourceAgentId ?? cfg.DeviceId,
                CreatedAt = t.CreatedAt.ToString("O"),
                UpdatedAt = t.UpdatedAt.ToString("O"),
            })
            .ToListAsync(ct);

        var preAuths = await db.PreAuths
            .AsNoTracking()
            .Where(p => p.ReplicationSeq > sinceSeq)
            .OrderBy(p => p.ReplicationSeq)
            .Take(limit)
            .Select(p => new ReplicatedPreAuth
            {
                Id = p.Id,
                SiteCode = p.SiteCode,
                OdooOrderId = p.OdooOrderId,
                PumpNumber = p.PumpNumber,
                NozzleNumber = p.NozzleNumber,
                ProductCode = p.ProductCode,
                RequestedAmount = p.RequestedAmount,
                UnitPrice = p.UnitPrice,
                Currency = p.Currency,
                Status = p.Status.ToString(),
                RequestedAt = p.RequestedAt.ToString("O"),
                ExpiresAt = p.ExpiresAt.ToString("O"),
                FccCorrelationId = p.FccCorrelationId,
                FccAuthorizationCode = p.FccAuthorizationCode,
                ReplicationSeq = p.ReplicationSeq,
                SourceAgentId = p.SourceAgentId ?? cfg.DeviceId,
                CreatedAt = p.CreatedAt.ToString("O"),
                UpdatedAt = p.UpdatedAt.ToString("O"),
            })
            .ToListAsync(ct);

        var maxTxSeq = transactions.Count > 0 ? transactions[^1].ReplicationSeq : sinceSeq;
        var maxPaSeq = preAuths.Count > 0 ? preAuths[^1].ReplicationSeq : sinceSeq;
        var toSeq = Math.Max(maxTxSeq, maxPaSeq);

        // HasMore if we hit the limit on either side
        var hasMore = transactions.Count >= limit || preAuths.Count >= limit;

        _logger.LogDebug(
            "Delta generated: sinceSeq={SinceSeq} toSeq={ToSeq} txCount={TxCount} paCount={PaCount} hasMore={HasMore}",
            sinceSeq, toSeq, transactions.Count, preAuths.Count, hasMore);

        return new DeltaSyncPayload
        {
            PrimaryAgentId = cfg.DeviceId,
            Epoch = _sequenceAssignor.CurrentSequence,
            FromSeq = sinceSeq,
            ToSeq = toSeq,
            Transactions = transactions,
            PreAuths = preAuths,
            HasMore = hasMore,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
    }
}

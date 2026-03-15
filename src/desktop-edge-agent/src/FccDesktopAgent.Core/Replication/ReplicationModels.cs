namespace FccDesktopAgent.Core.Replication;

// ── Snapshot (full bootstrap for new standby) ───────────────────────────────

public sealed class SnapshotPayload
{
    public string PrimaryAgentId { get; set; } = string.Empty;
    public long Epoch { get; set; }
    public string? ConfigVersion { get; set; }
    public long HighWaterMarkTxSeq { get; set; }
    public long HighWaterMarkPaSeq { get; set; }
    public List<ReplicatedTransaction> Transactions { get; set; } = [];
    public List<ReplicatedPreAuth> PreAuths { get; set; } = [];
    public List<ReplicatedNozzle> Nozzles { get; set; } = [];
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ── Delta (incremental sync) ────────────────────────────────────────────────

public sealed class DeltaSyncPayload
{
    public string PrimaryAgentId { get; set; } = string.Empty;
    public long Epoch { get; set; }
    public long FromSeq { get; set; }
    public long ToSeq { get; set; }
    public List<ReplicatedTransaction> Transactions { get; set; } = [];
    public List<ReplicatedPreAuth> PreAuths { get; set; } = [];
    public bool HasMore { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ── Replicated entity DTOs ──────────────────────────────────────────────────

public sealed class ReplicatedTransaction
{
    public string Id { get; set; } = string.Empty;
    public string FccTransactionId { get; set; } = string.Empty;
    public string SiteCode { get; set; } = string.Empty;
    public int PumpNumber { get; set; }
    public int NozzleNumber { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public long VolumeMicrolitres { get; set; }
    public long AmountMinorUnits { get; set; }
    public long UnitPriceMinorPerLitre { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public string StartedAt { get; set; } = string.Empty;
    public string CompletedAt { get; set; } = string.Empty;
    public string? FiscalReceiptNumber { get; set; }
    public string FccVendor { get; set; } = string.Empty;
    public string? AttendantId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string SyncStatus { get; set; } = string.Empty;
    public string IngestionSource { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public string? PreAuthId { get; set; }
    public long ReplicationSeq { get; set; }
    public string SourceAgentId { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public sealed class ReplicatedPreAuth
{
    public string Id { get; set; } = string.Empty;
    public string SiteCode { get; set; } = string.Empty;
    public string OdooOrderId { get; set; } = string.Empty;
    public int PumpNumber { get; set; }
    public int NozzleNumber { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public long RequestedAmount { get; set; }
    public long UnitPrice { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string RequestedAt { get; set; } = string.Empty;
    public string ExpiresAt { get; set; } = string.Empty;
    public string? FccCorrelationId { get; set; }
    public string? FccAuthorizationCode { get; set; }
    public long ReplicationSeq { get; set; }
    public string SourceAgentId { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public sealed class ReplicatedNozzle
{
    public string Id { get; set; } = string.Empty;
    public string SiteCode { get; set; } = string.Empty;
    public int FccPumpNumber { get; set; }
    public int FccNozzleNumber { get; set; }
    public int OdooPumpNumber { get; set; }
    public int OdooNozzleNumber { get; set; }
    public string ProductCode { get; set; } = string.Empty;
}

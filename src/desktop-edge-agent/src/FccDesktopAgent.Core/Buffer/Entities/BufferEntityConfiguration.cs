using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.PreAuth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FccDesktopAgent.Core.Buffer.Entities;

// Shared converters for DateTimeOffset ↔ ISO 8601 UTC text in SQLite
file static class Converters
{
    internal static readonly ValueConverter<DateTimeOffset, string> Required = new(
        v => v.ToString("O"),
        s => DateTimeOffset.Parse(s));

    internal static readonly ValueConverter<DateTimeOffset?, string?> Optional = new(
        v => v.HasValue ? v.Value.ToString("O") : null,
        s => s != null ? DateTimeOffset.Parse(s) : (DateTimeOffset?)null);
}

internal sealed class BufferedTransactionConfiguration : IEntityTypeConfiguration<BufferedTransaction>
{
    public void Configure(EntityTypeBuilder<BufferedTransaction> builder)
    {
        builder.ToTable("buffered_transactions");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).IsRequired().HasMaxLength(36);
        builder.Property(t => t.FccTransactionId).IsRequired().HasMaxLength(128);
        builder.Property(t => t.SiteCode).IsRequired().HasMaxLength(64);
        builder.Property(t => t.ProductCode).IsRequired().HasMaxLength(64);
        builder.Property(t => t.CurrencyCode).IsRequired().HasMaxLength(8);
        builder.Property(t => t.FccVendor).IsRequired().HasMaxLength(32);
        builder.Property(t => t.IngestionSource).IsRequired().HasMaxLength(32);
        builder.Property(t => t.SchemaVersion).IsRequired().HasMaxLength(16);
        builder.Property(t => t.FiscalReceiptNumber).HasMaxLength(64);
        builder.Property(t => t.AttendantId).HasMaxLength(64);
        builder.Property(t => t.CorrelationId).HasMaxLength(36);
        builder.Property(t => t.LastUploadError).HasMaxLength(512);

        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(t => t.SyncStatus).HasConversion<string>().HasMaxLength(32);

        builder.Property(t => t.StartedAt).HasConversion(Converters.Required);
        builder.Property(t => t.CompletedAt).HasConversion(Converters.Required);
        builder.Property(t => t.CreatedAt).HasConversion(Converters.Required);
        builder.Property(t => t.UpdatedAt).HasConversion(Converters.Required);
        builder.Property(t => t.LastUploadAttemptAt).HasConversion(Converters.Optional);
        builder.Property(t => t.OrderUuid).HasMaxLength(128);
        builder.Property(t => t.OdooOrderId).HasMaxLength(128);
        builder.Property(t => t.PreAuthId).HasMaxLength(36);
        builder.Property(t => t.PaymentId).HasMaxLength(128);
        builder.Property(t => t.AcknowledgedAt).HasConversion(Converters.Optional);
        builder.Property(t => t.FiscalStatus).IsRequired().HasMaxLength(16).HasDefaultValue("NONE");
        builder.Property(t => t.LastFiscalAttemptAt).HasConversion(Converters.Optional);

        // Replication columns
        builder.Property(t => t.ReplicationSeq).HasDefaultValue(0L);
        builder.Property(t => t.SourceAgentId).HasMaxLength(36);
        builder.Property(t => t.ReplicatedAt).HasConversion(Converters.Optional);

        // ix_bt_dedup — UNIQUE(FccTransactionId, SiteCode)
        builder.HasIndex(t => new { t.FccTransactionId, t.SiteCode })
            .IsUnique()
            .HasDatabaseName("ix_bt_dedup");

        // ix_bt_sync_status — upload replay scan
        builder.HasIndex(t => new { t.SyncStatus, t.CreatedAt })
            .HasDatabaseName("ix_bt_sync_status");

        // ix_bt_local_api — local API query: filter + sort CompletedAt DESC
        builder.HasIndex(t => new { t.SyncStatus, t.PumpNumber, t.CompletedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_bt_local_api");

        // ix_bt_cleanup — cleanup sweep by age
        builder.HasIndex(t => new { t.SyncStatus, t.UpdatedAt })
            .HasDatabaseName("ix_bt_cleanup");

        // ix_bt_repl_seq — replication sequence scan
        builder.HasIndex(t => t.ReplicationSeq)
            .HasDatabaseName("ix_bt_repl_seq");
    }
}

internal sealed class PreAuthRecordConfiguration : IEntityTypeConfiguration<PreAuthRecord>
{
    public void Configure(EntityTypeBuilder<PreAuthRecord> builder)
    {
        builder.ToTable("pre_auth_records");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id).IsRequired().HasMaxLength(36);
        builder.Property(p => p.SiteCode).IsRequired().HasMaxLength(64);
        builder.Property(p => p.OdooOrderId).IsRequired().HasMaxLength(128);
        builder.Property(p => p.ProductCode).IsRequired().HasMaxLength(64);
        builder.Property(p => p.Currency).IsRequired().HasMaxLength(8);
        builder.Property(p => p.SchemaVersion).IsRequired().HasMaxLength(16);
        builder.Property(p => p.VehicleNumber).HasMaxLength(64);
        builder.Property(p => p.CustomerName).HasMaxLength(128);
        builder.Property(p => p.CustomerTaxId).HasMaxLength(64);
        builder.Property(p => p.CustomerBusinessName).HasMaxLength(128);
        builder.Property(p => p.AttendantId).HasMaxLength(64);
        builder.Property(p => p.FailureReason).HasMaxLength(64);
        builder.Property(p => p.FccCorrelationId).HasMaxLength(128);
        builder.Property(p => p.FccAuthorizationCode).HasMaxLength(128);
        builder.Property(p => p.MatchedFccTransactionId).HasMaxLength(36);

        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);

        builder.Property(p => p.RequestedAt).HasConversion(Converters.Required);
        builder.Property(p => p.ExpiresAt).HasConversion(Converters.Required);
        builder.Property(p => p.CreatedAt).HasConversion(Converters.Required);
        builder.Property(p => p.UpdatedAt).HasConversion(Converters.Required);
        builder.Property(p => p.AuthorizedAt).HasConversion(Converters.Optional);
        builder.Property(p => p.DispensingAt).HasConversion(Converters.Optional);
        builder.Property(p => p.CompletedAt).HasConversion(Converters.Optional);
        builder.Property(p => p.CancelledAt).HasConversion(Converters.Optional);
        builder.Property(p => p.ExpiredAt).HasConversion(Converters.Optional);
        builder.Property(p => p.FailedAt).HasConversion(Converters.Optional);

        // Replication columns
        builder.Property(p => p.ReplicationSeq).HasDefaultValue(0L);
        builder.Property(p => p.SourceAgentId).HasMaxLength(36);
        builder.Property(p => p.ReplicatedAt).HasConversion(Converters.Optional);

        var activeStatuses = string.Join("','", PreAuthStateMachine.ActiveStatusNames);

        // ix_par_idemp — UNIQUE(OdooOrderId, SiteCode) for active records only
        builder.HasIndex(p => new { p.OdooOrderId, p.SiteCode })
            .IsUnique()
            .HasFilter($"\"Status\" IN ('{activeStatuses}')")
            .HasDatabaseName("ix_par_idemp");

        // ix_par_unsent — pending cloud sync scan
        builder.HasIndex(p => new { p.IsCloudSynced, p.CreatedAt })
            .HasDatabaseName("ix_par_unsent");

        // ix_par_expiry — expiry worker scan
        builder.HasIndex(p => new { p.Status, p.ExpiresAt })
            .HasDatabaseName("ix_par_expiry");

        // ix_par_repl_seq — replication sequence scan
        builder.HasIndex(p => p.ReplicationSeq)
            .HasDatabaseName("ix_par_repl_seq");
    }
}

internal sealed class NozzleMappingConfiguration : IEntityTypeConfiguration<NozzleMapping>
{
    public void Configure(EntityTypeBuilder<NozzleMapping> builder)
    {
        builder.ToTable("nozzles");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Id).IsRequired().HasMaxLength(36);
        builder.Property(n => n.SiteCode).IsRequired().HasMaxLength(64);
        builder.Property(n => n.ProductCode).IsRequired().HasMaxLength(64);

        builder.Property(n => n.SyncedAt).HasConversion(Converters.Optional);
        builder.Property(n => n.CreatedAt).HasConversion(Converters.Required);
        builder.Property(n => n.UpdatedAt).HasConversion(Converters.Required);

        // ix_nozzles_odoo_lookup — Odoo→FCC translation
        builder.HasIndex(n => new { n.SiteCode, n.OdooPumpNumber, n.OdooNozzleNumber })
            .IsUnique()
            .HasDatabaseName("ix_nozzles_odoo_lookup");

        // ix_nozzles_fcc_lookup — FCC→Odoo reverse lookup
        builder.HasIndex(n => new { n.SiteCode, n.FccPumpNumber, n.FccNozzleNumber })
            .IsUnique()
            .HasDatabaseName("ix_nozzles_fcc_lookup");
    }
}

internal sealed class SyncStateRecordConfiguration : IEntityTypeConfiguration<SyncStateRecord>
{
    public void Configure(EntityTypeBuilder<SyncStateRecord> builder)
    {
        builder.ToTable("sync_state");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.LastFccSequence).HasMaxLength(128);
        builder.Property(s => s.ConfigVersion).HasMaxLength(64);

        builder.Property(s => s.LastUploadAt).HasConversion(Converters.Optional);
        builder.Property(s => s.LastStatusSyncAt).HasConversion(Converters.Optional);
        builder.Property(s => s.LastConfigSyncAt).HasConversion(Converters.Optional);
        builder.Property(s => s.UpdatedAt).HasConversion(Converters.Required);
    }
}

internal sealed class AgentConfigRecordConfiguration : IEntityTypeConfiguration<AgentConfigRecord>
{
    public void Configure(EntityTypeBuilder<AgentConfigRecord> builder)
    {
        builder.ToTable("agent_config");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.ConfigJson).IsRequired();
        builder.Property(a => a.ConfigVersion).HasMaxLength(64);

        builder.Property(a => a.AppliedAt).HasConversion(Converters.Optional);
        builder.Property(a => a.UpdatedAt).HasConversion(Converters.Required);
    }
}

internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("audit_log");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedOnAdd();

        builder.Property(a => a.EventType).IsRequired().HasMaxLength(64);
        builder.Property(a => a.EntityType).HasMaxLength(64);
        builder.Property(a => a.EntityId).HasMaxLength(36);
        builder.Property(a => a.Actor).HasMaxLength(128);

        builder.Property(a => a.CreatedAt).HasConversion(Converters.Required);

        // ix_al_time — time-based query / cleanup
        builder.HasIndex(a => a.CreatedAt)
            .HasDatabaseName("ix_al_time");
    }
}

internal sealed class PumpLimitConfiguration : IEntityTypeConfiguration<PumpLimit>
{
    public void Configure(EntityTypeBuilder<PumpLimit> builder)
    {
        builder.ToTable("pump_limits");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedOnAdd();
        builder.Property(l => l.Status).IsRequired().HasMaxLength(16);
        builder.Property(l => l.UpdatedAt).HasConversion(Converters.Required);
        builder.HasIndex(l => l.FpId).IsUnique().HasDatabaseName("ix_pl_fpid");
    }
}

internal sealed class PumpBlockHistoryConfiguration : IEntityTypeConfiguration<PumpBlockHistory>
{
    public void Configure(EntityTypeBuilder<PumpBlockHistory> builder)
    {
        builder.ToTable("pump_block_history");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedOnAdd();
        builder.Property(h => h.ActionType).IsRequired().HasMaxLength(16);
        builder.Property(h => h.Source).IsRequired().HasMaxLength(32);
        builder.Property(h => h.Note).HasMaxLength(256);
        builder.Property(h => h.Timestamp).HasConversion(Converters.Required);
        builder.Property(h => h.SyncedAtUtc).HasConversion(Converters.Optional);
        builder.HasIndex(h => new { h.FpId, h.Timestamp }).HasDatabaseName("ix_pbh_fp_time");
        builder.HasIndex(h => h.IsSynced).HasDatabaseName("ix_pbh_synced");
    }
}

internal sealed class AttendantPumpCountConfiguration : IEntityTypeConfiguration<AttendantPumpCount>
{
    public void Configure(EntityTypeBuilder<AttendantPumpCount> builder)
    {
        builder.ToTable("attendant_pump_counts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedOnAdd();
        builder.Property(a => a.SessionId).IsRequired().HasMaxLength(64);
        builder.Property(a => a.EmpTagNo).IsRequired().HasMaxLength(64);
        builder.Property(a => a.CreatedAt).HasConversion(Converters.Required);
        builder.Property(a => a.UpdatedAt).HasConversion(Converters.Required);
        builder.HasIndex(a => new { a.SessionId, a.PumpNumber }).IsUnique().HasDatabaseName("ix_apc_session_pump");
    }
}

internal sealed class BufferedBnaReportConfiguration : IEntityTypeConfiguration<BufferedBnaReport>
{
    public void Configure(EntityTypeBuilder<BufferedBnaReport> builder)
    {
        builder.ToTable("bna_reports");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedOnAdd();
        builder.Property(b => b.TerminalId).IsRequired().HasMaxLength(64);
        builder.Property(b => b.ReportedAtUtc).HasConversion(Converters.Required);
        builder.Property(b => b.CreatedAt).HasConversion(Converters.Required);
        builder.HasIndex(b => b.IsSynced).HasDatabaseName("ix_bna_synced");
    }
}

internal sealed class BufferedPumpTotalsSnapshotConfiguration : IEntityTypeConfiguration<BufferedPumpTotalsSnapshot>
{
    public void Configure(EntityTypeBuilder<BufferedPumpTotalsSnapshot> builder)
    {
        builder.ToTable("pump_totals_snapshots");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedOnAdd();
        builder.Property(t => t.CurrencyCode).IsRequired().HasMaxLength(8);
        builder.Property(t => t.ObservedAtUtc).HasConversion(Converters.Required);
        builder.Property(t => t.SyncedAtUtc).HasConversion(Converters.Optional);
        builder.Property(t => t.CreatedAt).HasConversion(Converters.Required);
        builder.HasIndex(t => t.IsSynced).HasDatabaseName("ix_pts_synced");
        builder.HasIndex(t => new { t.PumpNumber, t.ObservedAtUtc }).HasDatabaseName("ix_pts_pump_time");
    }
}

internal sealed class BufferedPriceSnapshotConfiguration : IEntityTypeConfiguration<BufferedPriceSnapshot>
{
    public void Configure(EntityTypeBuilder<BufferedPriceSnapshot> builder)
    {
        builder.ToTable("price_snapshots_buffer");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
        builder.Property(p => p.PriceSetId).IsRequired().HasMaxLength(32);
        builder.Property(p => p.GradeId).IsRequired().HasMaxLength(64);
        builder.Property(p => p.GradeName).IsRequired().HasMaxLength(128);
        builder.Property(p => p.CurrencyCode).IsRequired().HasMaxLength(8);
        builder.Property(p => p.ObservedAtUtc).HasConversion(Converters.Required);
        builder.Property(p => p.SyncedAtUtc).HasConversion(Converters.Optional);
        builder.Property(p => p.CreatedAt).HasConversion(Converters.Required);
        builder.HasIndex(p => p.IsSynced).HasDatabaseName("ix_psb_synced");
        builder.HasIndex(p => new { p.GradeId, p.ObservedAtUtc }).HasDatabaseName("ix_psb_grade_time");
    }
}

internal sealed class DiagnosticLogCursorRecordConfiguration : IEntityTypeConfiguration<DiagnosticLogCursorRecord>
{
    public void Configure(EntityTypeBuilder<DiagnosticLogCursorRecord> builder)
    {
        builder.ToTable("diagnostic_log_cursor");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.FilePath).HasMaxLength(512);
        builder.Property(c => c.UpdatedAt).HasConversion(Converters.Required);
    }
}

internal sealed class ReplicationStateRecordConfiguration : IEntityTypeConfiguration<ReplicationStateRecord>
{
    public void Configure(EntityTypeBuilder<ReplicationStateRecord> builder)
    {
        builder.ToTable("replication_state");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.PrimaryAgentId).HasMaxLength(36);
        builder.Property(r => r.ConfigVersion).HasMaxLength(64);

        builder.Property(r => r.LastSnapshotAt).HasConversion(Converters.Optional);
        builder.Property(r => r.LastDeltaSyncAt).HasConversion(Converters.Optional);
        builder.Property(r => r.UpdatedAt).HasConversion(Converters.Required);
    }
}

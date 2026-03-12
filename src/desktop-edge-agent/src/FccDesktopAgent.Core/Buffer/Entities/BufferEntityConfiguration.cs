using FccDesktopAgent.Core.Adapter.Common;
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
        builder.Property(t => t.OdooOrderId).HasMaxLength(128);
        builder.Property(t => t.AcknowledgedAt).HasConversion(Converters.Optional);

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

        // ix_par_idemp — UNIQUE(OdooOrderId, SiteCode)
        builder.HasIndex(p => new { p.OdooOrderId, p.SiteCode })
            .IsUnique()
            .HasDatabaseName("ix_par_idemp");

        // ix_par_unsent — pending cloud sync scan
        builder.HasIndex(p => new { p.IsCloudSynced, p.CreatedAt })
            .HasDatabaseName("ix_par_unsent");

        // ix_par_expiry — expiry worker scan
        builder.HasIndex(p => new { p.Status, p.ExpiresAt })
            .HasDatabaseName("ix_par_expiry");
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

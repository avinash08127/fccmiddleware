using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class AgentTelemetrySnapshotConfiguration : IEntityTypeConfiguration<AgentTelemetrySnapshot>
{
    public void Configure(EntityTypeBuilder<AgentTelemetrySnapshot> builder)
    {
        builder.ToTable("agent_telemetry_snapshots");

        builder.HasKey(e => e.DeviceId);

        builder.Property(e => e.DeviceId).HasColumnName("device_id");
        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.SiteCode).HasColumnName("site_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.ReportedAtUtc).HasColumnName("reported_at_utc").IsRequired();
        builder.Property(e => e.ConnectivityState)
            .HasColumnName("connectivity_state")
            .HasMaxLength(30)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(e => e.PayloadJson)
            .HasColumnName("payload_json")
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(e => e.BatteryPercent).HasColumnName("battery_percent").IsRequired();
        builder.Property(e => e.IsCharging).HasColumnName("is_charging").IsRequired();
        builder.Property(e => e.PendingUploadCount).HasColumnName("pending_upload_count").IsRequired();
        builder.Property(e => e.SyncLagSeconds).HasColumnName("sync_lag_seconds");
        builder.Property(e => e.LastHeartbeatAtUtc).HasColumnName("last_heartbeat_at_utc");
        builder.Property(e => e.HeartbeatAgeSeconds).HasColumnName("heartbeat_age_seconds");
        builder.Property(e => e.FccVendor)
            .HasColumnName("fcc_vendor")
            .HasMaxLength(30)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(e => e.FccHost).HasColumnName("fcc_host").HasMaxLength(200).IsRequired();
        builder.Property(e => e.FccPort).HasColumnName("fcc_port").IsRequired();
        builder.Property(e => e.ConsecutiveHeartbeatFailures).HasColumnName("consecutive_heartbeat_failures").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasIndex(e => new { e.LegalEntityId, e.SiteCode })
            .HasDatabaseName("ix_agent_telemetry_legal_entity_site");
    }
}

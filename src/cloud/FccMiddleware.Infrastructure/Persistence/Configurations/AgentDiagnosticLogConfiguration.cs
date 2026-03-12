using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

public sealed class AgentDiagnosticLogConfiguration : IEntityTypeConfiguration<AgentDiagnosticLog>
{
    public void Configure(EntityTypeBuilder<AgentDiagnosticLog> builder)
    {
        builder.ToTable("agent_diagnostic_logs");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.DeviceId)
            .HasColumnName("device_id")
            .IsRequired();

        builder.Property(e => e.LegalEntityId)
            .HasColumnName("legal_entity_id")
            .IsRequired();

        builder.Property(e => e.SiteCode)
            .HasColumnName("site_code")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(e => e.UploadedAtUtc)
            .HasColumnName("uploaded_at_utc")
            .IsRequired();

        builder.Property(e => e.LogEntriesJson)
            .HasColumnName("log_entries_json")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()");

        builder.HasIndex(e => new { e.DeviceId, e.CreatedAt })
            .HasDatabaseName("ix_agent_diagnostic_logs_device_created");

        builder.HasIndex(e => new { e.LegalEntityId, e.SiteCode })
            .HasDatabaseName("ix_agent_diagnostic_logs_lei_site");
    }
}

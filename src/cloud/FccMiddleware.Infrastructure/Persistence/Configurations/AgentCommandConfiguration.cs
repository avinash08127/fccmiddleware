using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class AgentCommandConfiguration : IEntityTypeConfiguration<AgentCommand>
{
    public void Configure(EntityTypeBuilder<AgentCommand> builder)
    {
        builder.ToTable("agent_commands");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.SiteCode).HasColumnName("site_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.CommandType).HasColumnName("command_type").HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
        builder.Property(e => e.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
        builder.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(30)
            .HasDefaultValue(AgentCommandStatus.PENDING).IsRequired();
        builder.Property(e => e.CreatedByActorId).HasColumnName("created_by_actor_id").HasMaxLength(200);
        builder.Property(e => e.CreatedByActorDisplay).HasColumnName("created_by_actor_display").HasMaxLength(200);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(e => e.AcknowledgedAt).HasColumnName("acked_at");
        builder.Property(e => e.HandledAtUtc).HasColumnName("handled_at_utc");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0).IsRequired();
        builder.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(1000);
        builder.Property(e => e.ResultJson).HasColumnName("result_json").HasColumnType("jsonb");
        builder.Property(e => e.FailureCode).HasColumnName("failure_code").HasMaxLength(100);
        builder.Property(e => e.FailureMessage).HasColumnName("failure_message").HasMaxLength(1000);

        builder.HasOne(e => e.Device)
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .HasConstraintName("fk_agent_command_device");

        builder.HasIndex(e => new { e.DeviceId, e.Status, e.CreatedAt })
            .HasDatabaseName("ix_agent_commands_device_status_created");

        builder.HasIndex(e => new { e.LegalEntityId, e.SiteCode, e.CreatedAt })
            .HasDatabaseName("ix_agent_commands_tenant_site_created");

        builder.HasIndex(e => new { e.DeviceId, e.ExpiresAt })
            .HasDatabaseName("ix_agent_commands_device_expiry");

        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}

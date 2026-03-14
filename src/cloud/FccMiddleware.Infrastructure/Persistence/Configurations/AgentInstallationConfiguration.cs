using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class AgentInstallationConfiguration : IEntityTypeConfiguration<AgentInstallation>
{
    public void Configure(EntityTypeBuilder<AgentInstallation> builder)
    {
        builder.ToTable("agent_installations");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.SiteCode).HasColumnName("site_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.Platform).HasColumnName("platform").HasMaxLength(30).IsRequired();
        builder.Property(e => e.PushProvider).HasColumnName("push_provider").HasMaxLength(30).IsRequired();
        builder.Property(e => e.RegistrationToken).HasColumnName("registration_token_ciphertext").HasMaxLength(8192).IsRequired();
        builder.Property(e => e.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
        builder.Property(e => e.AppVersion).HasColumnName("app_version").HasMaxLength(50).IsRequired();
        builder.Property(e => e.OsVersion).HasColumnName("os_version").HasMaxLength(50).IsRequired();
        builder.Property(e => e.DeviceModel).HasColumnName("device_model").HasMaxLength(100).IsRequired();
        builder.Property(e => e.LastSeenAt).HasColumnName("last_seen_at").IsRequired();
        builder.Property(e => e.LastHintSentAt).HasColumnName("last_hint_sent_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(e => e.Device)
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .HasConstraintName("fk_agent_installation_device");

        builder.HasIndex(e => new { e.DeviceId, e.Platform, e.PushProvider })
            .HasDatabaseName("ix_agent_installations_device_platform");

        builder.HasIndex(e => e.TokenHash)
            .HasDatabaseName("ix_agent_installations_token_hash");
    }
}

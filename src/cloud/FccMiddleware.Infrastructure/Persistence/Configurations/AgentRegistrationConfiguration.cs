using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class AgentRegistrationConfiguration : IEntityTypeConfiguration<AgentRegistration>
{
    public void Configure(EntityTypeBuilder<AgentRegistration> builder)
    {
        builder.ToTable("agent_registrations");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.SiteId).HasColumnName("site_id").IsRequired();
        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.SiteCode).HasColumnName("site_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.DeviceSerialNumber).HasColumnName("device_serial_number").HasMaxLength(200).IsRequired();
        builder.Property(e => e.DeviceModel).HasColumnName("device_model").HasMaxLength(100).IsRequired();
        builder.Property(e => e.OsVersion).HasColumnName("os_version").HasMaxLength(50).IsRequired();
        builder.Property(e => e.AgentVersion).HasColumnName("agent_version").HasMaxLength(50).IsRequired();
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        builder.Property(e => e.TokenExpiresAt).HasColumnName("token_expires_at").IsRequired();
        builder.Property(e => e.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(e => e.RegisteredAt).HasColumnName("registered_at").IsRequired();
        builder.Property(e => e.DeactivatedAt).HasColumnName("deactivated_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(e => e.Site)
            .WithMany(s => s.AgentRegistrations)
            .HasForeignKey(e => e.SiteId)
            .HasConstraintName("fk_agent_reg_site");

        builder.HasOne(e => e.LegalEntity)
            .WithMany()
            .HasForeignKey(e => e.LegalEntityId)
            .HasConstraintName("fk_agent_reg_legal_entity");

        builder.HasIndex(e => new { e.SiteId, e.IsActive })
            .HasDatabaseName("ix_agent_site");

        builder.HasIndex(e => new { e.LegalEntityId, e.IsActive, e.RegisteredAt })
            .HasDatabaseName("ix_agent_legal_entity_active_registered");
    }
}

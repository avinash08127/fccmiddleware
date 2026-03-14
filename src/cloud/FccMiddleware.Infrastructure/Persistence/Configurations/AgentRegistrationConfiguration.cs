using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
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
        builder.Property(e => e.DeviceClass).HasColumnName("device_class").HasMaxLength(20).HasDefaultValue("ANDROID").IsRequired();
        builder.Property(e => e.RoleCapability).HasColumnName("role_capability").HasMaxLength(40).HasDefaultValue("PRIMARY_ELIGIBLE").IsRequired();
        builder.Property(e => e.SiteHaPriority).HasColumnName("site_ha_priority").HasDefaultValue(100).IsRequired();
        builder.Property(e => e.CurrentRole).HasColumnName("current_role").HasMaxLength(40);
        builder.Property(e => e.CapabilitiesJson).HasColumnName("capabilities_json").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").IsRequired();
        builder.Property(e => e.PeerApiBaseUrl).HasColumnName("peer_api_base_url").HasMaxLength(500);
        builder.Property(e => e.PeerApiAdvertisedHost).HasColumnName("peer_api_advertised_host").HasMaxLength(255);
        builder.Property(e => e.PeerApiPort).HasColumnName("peer_api_port");
        builder.Property(e => e.PeerApiTlsEnabled).HasColumnName("peer_api_tls_enabled").HasDefaultValue(false).IsRequired();
        builder.Property(e => e.LeaderEpochSeen).HasColumnName("leader_epoch_seen");
        builder.Property(e => e.LastReplicationLagSeconds).HasColumnName("last_replication_lag_seconds");
        builder.Property(e => e.Status)
            .HasColumnName("status")
            .HasMaxLength(40)
            .HasConversion<string>()
            .HasDefaultValue(AgentRegistrationStatus.ACTIVE)
            .IsRequired();
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
        builder.Property(e => e.TokenExpiresAt).HasColumnName("token_expires_at");
        builder.Property(e => e.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(e => e.RegisteredAt).HasColumnName("registered_at").IsRequired();
        builder.Property(e => e.DeactivatedAt).HasColumnName("deactivated_at");
        builder.Property(e => e.SuspensionReasonCode).HasColumnName("suspension_reason_code").HasMaxLength(100);
        builder.Property(e => e.SuspensionReason).HasColumnName("suspension_reason").HasMaxLength(500);
        builder.Property(e => e.ReplacementForDeviceId).HasColumnName("replacement_for_device_id");
        builder.Property(e => e.ApprovalGrantedAt).HasColumnName("approval_granted_at");
        builder.Property(e => e.ApprovalGrantedByActorId).HasColumnName("approval_granted_by_actor_id").HasMaxLength(200);
        builder.Property(e => e.ApprovalGrantedByActorDisplay).HasColumnName("approval_granted_by_actor_display").HasMaxLength(200);
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

        builder.HasIndex(e => new { e.SiteId, e.Status })
            .HasDatabaseName("ix_agent_site");

        builder.HasIndex(e => new { e.LegalEntityId, e.Status, e.RegisteredAt })
            .HasDatabaseName("ix_agent_legal_entity_status_registered");

        builder.HasIndex(e => new { e.SiteId, e.DeviceSerialNumber, e.Status })
            .HasDatabaseName("ix_agent_site_serial_status");

        // OB-T02: Use PostgreSQL xmin to prevent concurrent decommission requests
        // from both creating audit events against the same active registration.
        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}

using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class FccConfigConfiguration : IEntityTypeConfiguration<FccConfig>
{
    public void Configure(EntityTypeBuilder<FccConfig> builder)
    {
        builder.ToTable("fcc_configs");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.SiteId).HasColumnName("site_id").IsRequired();
        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.FccVendor)
            .HasColumnName("fcc_vendor")
            .HasMaxLength(30)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(e => e.FccModel).HasColumnName("fcc_model").HasMaxLength(100);
        builder.Property(e => e.ConnectionProtocol)
            .HasColumnName("connection_protocol")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(e => e.HostAddress).HasColumnName("host_address").HasMaxLength(200).IsRequired();
        builder.Property(e => e.Port).HasColumnName("port").IsRequired();
        builder.Property(e => e.CredentialRef).HasColumnName("credential_ref").HasMaxLength(200).IsRequired();
        builder.Property(e => e.IngestionMethod)
            .HasColumnName("transaction_mode")
            .HasMaxLength(20)
            .HasConversion<string>()
            .HasDefaultValue(IngestionMethod.PUSH)
            .IsRequired();
        builder.Property(e => e.IngestionMode)
            .HasColumnName("ingestion_mode")
            .HasMaxLength(20)
            .HasConversion<string>()
            .HasDefaultValue(IngestionMode.CLOUD_DIRECT)
            .IsRequired();
        builder.Property(e => e.PullIntervalSeconds).HasColumnName("pull_interval_seconds");
        builder.Property(e => e.CatchUpPullIntervalSeconds).HasColumnName("catchup_pull_interval_seconds");
        builder.Property(e => e.HybridCatchUpIntervalSeconds).HasColumnName("hybrid_catchup_interval_seconds");
        builder.Property(e => e.HeartbeatIntervalSeconds).HasColumnName("heartbeat_interval_seconds").HasDefaultValue(60).IsRequired();
        builder.Property(e => e.HeartbeatTimeoutSeconds).HasColumnName("heartbeat_timeout_seconds").HasDefaultValue(180).IsRequired();
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.ConfigVersion).HasColumnName("config_version").HasDefaultValue(1).IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        // ── Radix-specific fields ────────────────────────────────────────────
        builder.Property(e => e.SharedSecret).HasColumnName("shared_secret").HasMaxLength(500);
        builder.Property(e => e.UsnCode).HasColumnName("usn_code");
        builder.Property(e => e.AuthPort).HasColumnName("auth_port");
        builder.Property(e => e.FccPumpAddressMap).HasColumnName("fcc_pump_address_map");

        // ── DOMS TCP/JPL fields ──────────────────────────────────────────────
        builder.Property(e => e.JplPort).HasColumnName("jpl_port");
        builder.Property(e => e.FcAccessCode).HasColumnName("fc_access_code").HasMaxLength(500);
        builder.Property(e => e.DomsCountryCode).HasColumnName("doms_country_code").HasMaxLength(10);
        builder.Property(e => e.PosVersionId).HasColumnName("pos_version_id").HasMaxLength(50);
        builder.Property(e => e.ReconnectBackoffMaxSeconds).HasColumnName("reconnect_backoff_max_seconds");
        builder.Property(e => e.ConfiguredPumps).HasColumnName("configured_pumps").HasMaxLength(200);
        builder.Property(e => e.DppPorts).HasColumnName("dpp_ports").HasMaxLength(200);

        // ── Petronite OAuth2 fields ──────────────────────────────────────────
        builder.Property(e => e.ClientId).HasColumnName("client_id").HasMaxLength(500);
        builder.Property(e => e.ClientSecret).HasColumnName("client_secret").HasMaxLength(500);
        builder.Property(e => e.WebhookSecret).HasColumnName("webhook_secret").HasMaxLength(500);
        builder.Property(e => e.OAuthTokenEndpoint).HasColumnName("oauth_token_endpoint").HasMaxLength(500);

        // ── Advatec EFD fields ──────────────────────────────────────────────
        builder.Property(e => e.AdvatecDevicePort).HasColumnName("advatec_device_port");
        builder.Property(e => e.AdvatecWebhookToken).HasColumnName("advatec_webhook_token").HasMaxLength(500);
        builder.Property(e => e.AdvatecWebhookTokenHash).HasColumnName("advatec_webhook_token_hash").HasMaxLength(64);
        builder.Property(e => e.AdvatecEfdSerialNumber).HasColumnName("advatec_efd_serial_number").HasMaxLength(100);
        builder.Property(e => e.AdvatecCustIdType).HasColumnName("advatec_cust_id_type");
        builder.Property(e => e.AdvatecPumpMap).HasColumnName("advatec_pump_map");

        builder.HasOne(e => e.Site)
            .WithMany(s => s.FccConfigs)
            .HasForeignKey(e => e.SiteId)
            .HasConstraintName("fk_fcc_configs_site");

        builder.HasOne(e => e.LegalEntity)
            .WithMany()
            .HasForeignKey(e => e.LegalEntityId)
            .HasConstraintName("fk_fcc_configs_legal_entity");

        // H-04: Index on webhook token hash for O(1) lookup instead of full table scan
        builder.HasIndex(e => e.AdvatecWebhookTokenHash)
            .HasDatabaseName("ix_fcc_configs_advatec_webhook_token_hash")
            .HasFilter("advatec_webhook_token_hash IS NOT NULL");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("chk_fcc_transaction_mode", "transaction_mode IN ('PUSH','PULL','HYBRID')");
            t.HasCheckConstraint("chk_fcc_ingestion_mode", "ingestion_mode IN ('CLOUD_DIRECT','RELAY','BUFFER_ALWAYS')");
        });
    }
}

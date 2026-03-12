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
        builder.Property(e => e.HeartbeatIntervalSeconds).HasColumnName("heartbeat_interval_seconds").HasDefaultValue(60).IsRequired();
        builder.Property(e => e.HeartbeatTimeoutSeconds).HasColumnName("heartbeat_timeout_seconds").HasDefaultValue(180).IsRequired();
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.ConfigVersion).HasColumnName("config_version").HasDefaultValue(1).IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(e => e.Site)
            .WithMany(s => s.FccConfigs)
            .HasForeignKey(e => e.SiteId)
            .HasConstraintName("fk_fcc_configs_site");

        builder.HasOne(e => e.LegalEntity)
            .WithMany()
            .HasForeignKey(e => e.LegalEntityId)
            .HasConstraintName("fk_fcc_configs_legal_entity");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("chk_fcc_transaction_mode", "transaction_mode IN ('PUSH','PULL','HYBRID')");
            t.HasCheckConstraint("chk_fcc_ingestion_mode", "ingestion_mode IN ('CLOUD_DIRECT','RELAY','BUFFER_ALWAYS')");
        });
    }
}

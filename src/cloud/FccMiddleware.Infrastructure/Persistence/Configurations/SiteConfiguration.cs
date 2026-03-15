using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        builder.ToTable("sites");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.SiteCode).HasColumnName("site_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.SiteName).HasColumnName("site_name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.OperatingModel)
            .HasColumnName("operating_model")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(e => e.SiteUsesPreAuth).HasColumnName("site_uses_pre_auth").HasDefaultValue(false).IsRequired();
        builder.Property(e => e.AmountTolerancePercent).HasColumnName("amount_tolerance_percent").HasPrecision(5, 2);
        builder.Property(e => e.AmountToleranceAbsolute).HasColumnName("amount_tolerance_absolute");
        builder.Property(e => e.TimeWindowMinutes).HasColumnName("time_window_minutes");
        builder.Property(e => e.ConnectivityMode).HasColumnName("connectivity_mode").HasMaxLength(20).HasDefaultValue("CONNECTED").IsRequired();
        builder.Property(e => e.OperatorName).HasColumnName("operator_name").HasMaxLength(200);
        builder.Property(e => e.OperatorTaxPayerId).HasColumnName("operator_tax_payer_id").HasMaxLength(100);
        builder.Property(e => e.CompanyTaxPayerId).HasColumnName("company_tax_payer_id").HasMaxLength(100).IsRequired();
        builder.Property(e => e.FiscalizationMode)
            .HasColumnName("fiscalization_mode")
            .HasMaxLength(30)
            .HasConversion<string>()
            .HasDefaultValue(FiscalizationMode.NONE)
            .IsRequired();
        builder.Property(e => e.TaxAuthorityEndpoint).HasColumnName("tax_authority_endpoint").HasMaxLength(500);
        builder.Property(e => e.RequireCustomerTaxId).HasColumnName("require_customer_tax_id").HasDefaultValue(false).IsRequired();
        builder.Property(e => e.FiscalReceiptRequired).HasColumnName("fiscal_receipt_required").HasDefaultValue(false).IsRequired();
        builder.Property(e => e.OdooSiteId).HasColumnName("odoo_site_id").HasMaxLength(100);
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.DeactivatedAt).HasColumnName("deactivated_at");
        builder.Property(e => e.SyncedAt).HasColumnName("synced_at").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.PeerDirectoryVersion).HasColumnName("peer_directory_version").HasDefaultValue(0L).IsRequired();
        builder.Property(e => e.HaLeaderEpoch).HasColumnName("ha_leader_epoch").HasDefaultValue(0L).IsRequired();
        builder.Property(e => e.HaLeaderAgentId).HasColumnName("ha_leader_agent_id");

        builder.HasOne(e => e.LegalEntity)
            .WithMany(le => le.Sites)
            .HasForeignKey(e => e.LegalEntityId)
            .HasConstraintName("fk_sites_legal_entity");

        builder.HasIndex(e => e.SiteCode)
            .IsUnique()
            .HasDatabaseName("uq_sites_site_code");

        builder.HasIndex(e => e.LegalEntityId)
            .HasDatabaseName("ix_sites_legal_entity");

        builder.ToTable(t => t.HasCheckConstraint(
            "chk_sites_operating_model",
            "operating_model IN ('COCO','CODO','DODO','DOCO')"));
        builder.ToTable(t => t.HasCheckConstraint(
            "chk_sites_fiscalization_mode",
            "fiscalization_mode IN ('FCC_DIRECT','EXTERNAL_INTEGRATION','NONE')"));
    }
}

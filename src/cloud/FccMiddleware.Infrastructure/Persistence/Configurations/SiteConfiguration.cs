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
        builder.Property(e => e.ConnectivityMode).HasColumnName("connectivity_mode").HasMaxLength(20).HasDefaultValue("CONNECTED").IsRequired();
        builder.Property(e => e.OperatorName).HasColumnName("operator_name").HasMaxLength(200);
        builder.Property(e => e.OperatorTaxPayerId).HasColumnName("operator_tax_payer_id").HasMaxLength(100);
        builder.Property(e => e.CompanyTaxPayerId).HasColumnName("company_tax_payer_id").HasMaxLength(100).IsRequired();
        builder.Property(e => e.OdooSiteId).HasColumnName("odoo_site_id").HasMaxLength(100);
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.DeactivatedAt).HasColumnName("deactivated_at");
        builder.Property(e => e.SyncedAt).HasColumnName("synced_at").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

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
    }
}

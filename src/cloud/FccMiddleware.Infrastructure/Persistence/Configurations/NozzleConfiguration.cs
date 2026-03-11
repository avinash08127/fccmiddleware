using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class NozzleConfiguration : IEntityTypeConfiguration<Nozzle>
{
    public void Configure(EntityTypeBuilder<Nozzle> builder)
    {
        builder.ToTable("nozzles");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.PumpId).HasColumnName("pump_id").IsRequired();
        builder.Property(e => e.SiteId).HasColumnName("site_id").IsRequired();
        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.OdooNozzleNumber).HasColumnName("odoo_nozzle_number").IsRequired();
        builder.Property(e => e.FccNozzleNumber).HasColumnName("fcc_nozzle_number").IsRequired();
        builder.Property(e => e.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.SyncedAt).HasColumnName("synced_at").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(e => e.Pump)
            .WithMany(p => p.Nozzles)
            .HasForeignKey(e => e.PumpId)
            .HasConstraintName("fk_nozzles_pump");

        builder.HasOne(e => e.Site)
            .WithMany()
            .HasForeignKey(e => e.SiteId)
            .HasConstraintName("fk_nozzles_site");

        builder.HasOne(e => e.LegalEntity)
            .WithMany()
            .HasForeignKey(e => e.LegalEntityId)
            .HasConstraintName("fk_nozzles_legal_entity");

        builder.HasOne(e => e.Product)
            .WithMany(p => p.Nozzles)
            .HasForeignKey(e => e.ProductId)
            .HasConstraintName("fk_nozzles_product");

        builder.HasIndex(e => new { e.PumpId, e.OdooNozzleNumber })
            .IsUnique()
            .HasDatabaseName("uq_nozzles_pump_odoo");

        builder.HasIndex(e => new { e.PumpId, e.FccNozzleNumber })
            .IsUnique()
            .HasDatabaseName("uq_nozzles_pump_fcc");

        builder.HasIndex(e => new { e.PumpId, e.IsActive })
            .HasDatabaseName("ix_nozzles_pump");

        builder.HasIndex(e => new { e.SiteId, e.IsActive })
            .HasDatabaseName("ix_nozzles_site_lookup");
    }
}

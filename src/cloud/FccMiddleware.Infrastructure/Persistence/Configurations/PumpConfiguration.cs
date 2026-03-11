using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class PumpConfiguration : IEntityTypeConfiguration<Pump>
{
    public void Configure(EntityTypeBuilder<Pump> builder)
    {
        builder.ToTable("pumps");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.SiteId).HasColumnName("site_id").IsRequired();
        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.PumpNumber).HasColumnName("pump_number").IsRequired();
        builder.Property(e => e.FccPumpNumber).HasColumnName("fcc_pump_number").IsRequired();
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.SyncedAt).HasColumnName("synced_at").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(e => e.Site)
            .WithMany(s => s.Pumps)
            .HasForeignKey(e => e.SiteId)
            .HasConstraintName("fk_pumps_site");

        builder.HasOne(e => e.LegalEntity)
            .WithMany()
            .HasForeignKey(e => e.LegalEntityId)
            .HasConstraintName("fk_pumps_legal_entity");

        builder.HasIndex(e => new { e.SiteId, e.PumpNumber })
            .IsUnique()
            .HasDatabaseName("uq_pumps_site_odoo_pump");

        builder.HasIndex(e => new { e.SiteId, e.FccPumpNumber })
            .IsUnique()
            .HasDatabaseName("uq_pumps_site_fcc_pump");
    }
}

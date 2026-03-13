using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class SiteAdapterOverrideConfiguration : IEntityTypeConfiguration<SiteAdapterOverride>
{
    public void Configure(EntityTypeBuilder<SiteAdapterOverride> builder)
    {
        builder.ToTable("site_adapter_overrides");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.SiteId)
            .HasColumnName("site_id")
            .IsRequired();

        builder.Property(e => e.LegalEntityId)
            .HasColumnName("legal_entity_id")
            .IsRequired();

        builder.Property(e => e.AdapterKey)
            .HasColumnName("adapter_key")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(e => e.FccVendor)
            .HasColumnName("fcc_vendor")
            .HasMaxLength(30)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(e => e.OverrideJson)
            .HasColumnName("override_json")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(e => e.ConfigVersion)
            .HasColumnName("config_version")
            .HasDefaultValue(1)
            .IsRequired();

        builder.Property(e => e.UpdatedBy)
            .HasColumnName("updated_by")
            .HasMaxLength(200);

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()")
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("now()")
            .IsRequired();

        builder.HasIndex(e => new { e.SiteId, e.AdapterKey })
            .IsUnique()
            .HasDatabaseName("ux_site_adapter_overrides_site_adapter");

        builder.HasOne(e => e.Site)
            .WithMany()
            .HasForeignKey(e => e.SiteId)
            .HasConstraintName("fk_site_adapter_overrides_site");

        builder.HasOne(e => e.LegalEntity)
            .WithMany()
            .HasForeignKey(e => e.LegalEntityId)
            .HasConstraintName("fk_site_adapter_overrides_legal_entity");
    }
}

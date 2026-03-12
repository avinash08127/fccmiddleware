using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class LegalEntitySettingsOverrideConfiguration : IEntityTypeConfiguration<LegalEntitySettingsOverride>
{
    public void Configure(EntityTypeBuilder<LegalEntitySettingsOverride> builder)
    {
        builder.ToTable("legal_entity_settings_overrides");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.AmountTolerancePercent).HasColumnName("amount_tolerance_percent").HasPrecision(5, 2);
        builder.Property(e => e.AmountToleranceAbsoluteMinorUnits).HasColumnName("amount_tolerance_absolute_minor_units");
        builder.Property(e => e.TimeWindowMinutes).HasColumnName("time_window_minutes");
        builder.Property(e => e.StalePendingThresholdDays).HasColumnName("stale_pending_threshold_days");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedBy).HasColumnName("updated_by").HasMaxLength(200);

        builder.HasOne(e => e.LegalEntity)
            .WithMany()
            .HasForeignKey(e => e.LegalEntityId)
            .HasConstraintName("fk_legal_entity_settings_overrides_legal_entity");

        builder.HasIndex(e => e.LegalEntityId)
            .IsUnique()
            .HasDatabaseName("uq_legal_entity_settings_overrides_legal_entity_id");
    }
}

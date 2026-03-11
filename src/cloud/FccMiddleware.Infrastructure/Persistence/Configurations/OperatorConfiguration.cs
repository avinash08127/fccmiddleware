using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class OperatorConfiguration : IEntityTypeConfiguration<Operator>
{
    public void Configure(EntityTypeBuilder<Operator> builder)
    {
        builder.ToTable("operators");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.OperatorCode).HasColumnName("operator_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.OperatorName).HasColumnName("operator_name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.TaxPayerId).HasColumnName("tax_payer_id").HasMaxLength(100);
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.SyncedAt).HasColumnName("synced_at").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(e => e.LegalEntity)
            .WithMany(le => le.Operators)
            .HasForeignKey(e => e.LegalEntityId)
            .HasConstraintName("fk_operators_legal_entity");

        builder.HasIndex(e => new { e.LegalEntityId, e.OperatorCode })
            .IsUnique()
            .HasDatabaseName("uq_operators_entity_code");
    }
}

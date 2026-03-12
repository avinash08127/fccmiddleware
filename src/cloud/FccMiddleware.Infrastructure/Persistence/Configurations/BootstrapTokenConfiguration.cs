using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class BootstrapTokenConfiguration : IEntityTypeConfiguration<BootstrapToken>
{
    public void Configure(EntityTypeBuilder<BootstrapToken> builder)
    {
        builder.ToTable("bootstrap_tokens");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.SiteCode).HasColumnName("site_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
        builder.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(ProvisioningTokenStatus.ACTIVE).IsRequired();
        builder.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(200).IsRequired();
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(e => e.UsedAt).HasColumnName("used_at");
        builder.Property(e => e.UsedByDeviceId).HasColumnName("used_by_device_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.Environment).HasColumnName("environment").HasMaxLength(50);

        builder.HasOne(e => e.LegalEntity)
            .WithMany()
            .HasForeignKey(e => e.LegalEntityId)
            .HasConstraintName("fk_bootstrap_token_legal_entity");

        // Unique index on token hash for fast lookup during registration
        builder.HasIndex(e => e.TokenHash)
            .IsUnique()
            .HasDatabaseName("ix_bootstrap_token_hash");

        // BUG-007: Use PostgreSQL xmin as optimistic concurrency token to prevent
        // race condition where two concurrent registrations consume the same token.
        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}

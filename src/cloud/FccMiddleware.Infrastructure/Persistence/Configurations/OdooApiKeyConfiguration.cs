using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class OdooApiKeyConfiguration : IEntityTypeConfiguration<OdooApiKey>
{
    public void Configure(EntityTypeBuilder<OdooApiKey> builder)
    {
        builder.ToTable("odoo_api_keys");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.LegalEntityId)
            .HasColumnName("legal_entity_id")
            .IsRequired();

        builder.Property(e => e.KeyHash)
            .HasColumnName("key_hash")
            .HasMaxLength(64)   // SHA-256 hex = 64 chars
            .IsRequired();

        builder.Property(e => e.Label)
            .HasColumnName("label")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(e => e.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()")
            .IsRequired();

        builder.Property(e => e.ExpiresAt)
            .HasColumnName("expires_at");

        builder.Property(e => e.RevokedAt)
            .HasColumnName("revoked_at");

        // Fast auth lookup by hash (unique — one key hash maps to one record).
        builder.HasIndex(e => e.KeyHash)
            .IsUnique()
            .HasDatabaseName("ix_odoo_api_keys_hash");

        // List all keys for a legal entity (portal management).
        builder.HasIndex(e => e.LegalEntityId)
            .HasDatabaseName("ix_odoo_api_keys_lei");
    }
}

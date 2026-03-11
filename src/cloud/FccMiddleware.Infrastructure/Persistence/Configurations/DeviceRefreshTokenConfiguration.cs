using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class DeviceRefreshTokenConfiguration : IEntityTypeConfiguration<DeviceRefreshToken>
{
    public void Configure(EntityTypeBuilder<DeviceRefreshToken> builder)
    {
        builder.ToTable("device_refresh_tokens");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(e => e.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(e => e.RevokedAt).HasColumnName("revoked_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(e => e.Device)
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .HasConstraintName("fk_refresh_token_device");

        // Unique index on token hash for fast lookup during refresh
        builder.HasIndex(e => e.TokenHash)
            .IsUnique()
            .HasDatabaseName("ix_refresh_token_hash");

        // Index for revoking all tokens for a device (decommission)
        builder.HasIndex(e => new { e.DeviceId, e.RevokedAt })
            .HasDatabaseName("ix_refresh_token_device");
    }
}

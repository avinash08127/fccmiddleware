using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

public sealed class PortalUserConfiguration : IEntityTypeConfiguration<PortalUser>
{
    public void Configure(EntityTypeBuilder<PortalUser> builder)
    {
        builder.ToTable("portal_users");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
        builder.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(256).IsRequired();
        builder.Property(e => e.EntraObjectId).HasColumnName("entra_object_id").HasMaxLength(128);
        builder.Property(e => e.RoleId).HasColumnName("role_id").IsRequired();
        builder.Property(e => e.AllLegalEntities).HasColumnName("all_legal_entities").HasDefaultValue(false).IsRequired();
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true).IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(320);
        builder.Property(e => e.UpdatedBy).HasColumnName("updated_by").HasMaxLength(320);

        builder.HasIndex(e => e.Email).IsUnique();

        builder.HasOne(e => e.Role)
            .WithMany()
            .HasForeignKey(e => e.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

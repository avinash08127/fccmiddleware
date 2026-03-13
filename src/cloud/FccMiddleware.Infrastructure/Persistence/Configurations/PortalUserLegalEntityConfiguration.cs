using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

public sealed class PortalUserLegalEntityConfiguration : IEntityTypeConfiguration<PortalUserLegalEntity>
{
    public void Configure(EntityTypeBuilder<PortalUserLegalEntity> builder)
    {
        builder.ToTable("portal_user_legal_entities");

        builder.HasKey(e => new { e.UserId, e.LegalEntityId });
        builder.Property(e => e.UserId).HasColumnName("user_id");
        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");

        builder.HasOne(e => e.User)
            .WithMany(u => u.LegalEntityLinks)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.LegalEntity)
            .WithMany()
            .HasForeignKey(e => e.LegalEntityId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

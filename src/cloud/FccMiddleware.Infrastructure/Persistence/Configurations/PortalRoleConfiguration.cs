using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

public sealed class PortalRoleConfiguration : IEntityTypeConfiguration<PortalRole>
{
    public void Configure(EntityTypeBuilder<PortalRole> builder)
    {
        builder.ToTable("portal_roles");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(64).IsRequired();

        builder.HasIndex(e => e.Name).IsUnique();

        builder.HasData(
            new PortalRole { Id = PortalRole.FccAdmin, Name = "FccAdmin" },
            new PortalRole { Id = PortalRole.FccUser, Name = "FccUser" },
            new PortalRole { Id = PortalRole.FccViewer, Name = "FccViewer" });
    }
}

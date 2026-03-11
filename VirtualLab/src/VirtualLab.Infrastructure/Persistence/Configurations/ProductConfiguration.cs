using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProductCode).HasMaxLength(32);
        builder.Property(x => x.Name).HasMaxLength(128);
        builder.Property(x => x.Grade).HasMaxLength(32);
        builder.Property(x => x.ColorHex).HasMaxLength(16);
        builder.Property(x => x.CurrencyCode).HasMaxLength(8);
        builder.Property(x => x.UnitPrice).HasPrecision(18, 3);

        builder.HasOne(x => x.LabEnvironment)
            .WithMany(x => x.Products)
            .HasForeignKey(x => x.LabEnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.LabEnvironmentId, x.ProductCode }).IsUnique();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class NozzleConfiguration : IEntityTypeConfiguration<Nozzle>
{
    public void Configure(EntityTypeBuilder<Nozzle> builder)
    {
        builder.ToTable("Nozzles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Label).HasMaxLength(64);
        builder.Property(x => x.SimulationStateJson).HasColumnType("TEXT");

        builder.HasOne(x => x.Pump)
            .WithMany(x => x.Nozzles)
            .HasForeignKey(x => x.PumpId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Product)
            .WithMany(x => x.Nozzles)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.PumpId, x.NozzleNumber }).IsUnique();
        builder.HasIndex(x => new { x.ProductId, x.State });
    }
}

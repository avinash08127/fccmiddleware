using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class PumpConfiguration : IEntityTypeConfiguration<Pump>
{
    public void Configure(EntityTypeBuilder<Pump> builder)
    {
        builder.ToTable("Pumps");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Label).HasMaxLength(64);

        builder.HasOne(x => x.Site)
            .WithMany(x => x.Pumps)
            .HasForeignKey(x => x.SiteId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.SiteId, x.PumpNumber }).IsUnique();
        builder.HasIndex(x => new { x.SiteId, x.FccPumpNumber });
    }
}

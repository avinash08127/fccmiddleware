using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class LabEnvironmentConfiguration : IEntityTypeConfiguration<LabEnvironment>
{
    public void Configure(EntityTypeBuilder<LabEnvironment> builder)
    {
        builder.ToTable("LabEnvironments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Key).HasMaxLength(64);
        builder.Property(x => x.Name).HasMaxLength(128);
        builder.Property(x => x.Description).HasMaxLength(1024);
        builder.Property(x => x.SettingsJson).HasColumnType("TEXT");
        builder.HasIndex(x => x.Key).IsUnique();
    }
}

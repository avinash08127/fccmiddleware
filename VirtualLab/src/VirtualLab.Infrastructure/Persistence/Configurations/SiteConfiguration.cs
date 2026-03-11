using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        builder.ToTable("Sites");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SiteCode).HasMaxLength(32);
        builder.Property(x => x.Name).HasMaxLength(128);
        builder.Property(x => x.TimeZone).HasMaxLength(64);
        builder.Property(x => x.CurrencyCode).HasMaxLength(8);
        builder.Property(x => x.ExternalReference).HasMaxLength(128);
        builder.Property(x => x.ApiKeyHeaderName).HasMaxLength(64);
        builder.Property(x => x.ApiKeyValue).HasMaxLength(256);
        builder.Property(x => x.BasicAuthUsername).HasMaxLength(128);
        builder.Property(x => x.BasicAuthPassword).HasMaxLength(256);
        builder.Property(x => x.SettingsJson).HasColumnType("TEXT");

        builder.HasOne(x => x.LabEnvironment)
            .WithMany(x => x.Sites)
            .HasForeignKey(x => x.LabEnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.ActiveFccSimulatorProfile)
            .WithMany(x => x.Sites)
            .HasForeignKey(x => x.ActiveFccSimulatorProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.SiteCode).IsUnique();
        builder.HasIndex(x => new { x.LabEnvironmentId, x.ActiveFccSimulatorProfileId });
        builder.HasIndex(x => new { x.SiteCode, x.IsActive });
    }
}

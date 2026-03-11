using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class CallbackTargetConfiguration : IEntityTypeConfiguration<CallbackTarget>
{
    public void Configure(EntityTypeBuilder<CallbackTarget> builder)
    {
        builder.ToTable("CallbackTargets");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TargetKey).HasMaxLength(64);
        builder.Property(x => x.Name).HasMaxLength(128);
        builder.Property(x => x.CallbackUrl)
            .HasConversion(x => x.ToString(), x => new Uri(x))
            .HasMaxLength(512);
        builder.Property(x => x.ApiKeyHeaderName).HasMaxLength(64);
        builder.Property(x => x.ApiKeyValue).HasMaxLength(256);
        builder.Property(x => x.BasicAuthUsername).HasMaxLength(128);
        builder.Property(x => x.BasicAuthPassword).HasMaxLength(256);

        builder.HasOne(x => x.LabEnvironment)
            .WithMany(x => x.CallbackTargets)
            .HasForeignKey(x => x.LabEnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Site)
            .WithMany(x => x.CallbackTargets)
            .HasForeignKey(x => x.SiteId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => x.TargetKey).IsUnique();
        builder.HasIndex(x => new { x.SiteId, x.IsActive });
    }
}

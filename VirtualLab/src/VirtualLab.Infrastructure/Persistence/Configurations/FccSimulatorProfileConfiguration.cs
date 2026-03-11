using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class FccSimulatorProfileConfiguration : IEntityTypeConfiguration<FccSimulatorProfile>
{
    public void Configure(EntityTypeBuilder<FccSimulatorProfile> builder)
    {
        builder.ToTable("FccSimulatorProfiles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProfileKey).HasMaxLength(64);
        builder.Property(x => x.Name).HasMaxLength(128);
        builder.Property(x => x.VendorFamily).HasMaxLength(64);
        builder.Property(x => x.EndpointBasePath).HasMaxLength(128);
        builder.Property(x => x.EndpointSurfaceJson).HasColumnType("TEXT");
        builder.Property(x => x.AuthConfigurationJson).HasColumnType("TEXT");
        builder.Property(x => x.CapabilitiesJson).HasColumnType("TEXT");
        builder.Property(x => x.RequestTemplatesJson).HasColumnType("TEXT");
        builder.Property(x => x.ResponseTemplatesJson).HasColumnType("TEXT");
        builder.Property(x => x.ValidationRulesJson).HasColumnType("TEXT");
        builder.Property(x => x.FieldMappingsJson).HasColumnType("TEXT");
        builder.Property(x => x.FailureSimulationJson).HasColumnType("TEXT");
        builder.Property(x => x.ExtensionConfigurationJson).HasColumnType("TEXT");

        builder.HasOne(x => x.LabEnvironment)
            .WithMany(x => x.Profiles)
            .HasForeignKey(x => x.LabEnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.LabEnvironmentId, x.ProfileKey }).IsUnique();
        builder.HasIndex(x => new { x.LabEnvironmentId, x.AuthMode, x.DeliveryMode, x.PreAuthMode });
    }
}

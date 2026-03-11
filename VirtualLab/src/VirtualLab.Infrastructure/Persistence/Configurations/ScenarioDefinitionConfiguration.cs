using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class ScenarioDefinitionConfiguration : IEntityTypeConfiguration<ScenarioDefinition>
{
    public void Configure(EntityTypeBuilder<ScenarioDefinition> builder)
    {
        builder.ToTable("ScenarioDefinitions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ScenarioKey).HasMaxLength(64);
        builder.Property(x => x.Name).HasMaxLength(128);
        builder.Property(x => x.Description).HasMaxLength(1024);
        builder.Property(x => x.DefinitionJson).HasColumnType("TEXT");
        builder.Property(x => x.ReplaySignature).HasMaxLength(128);

        builder.HasOne(x => x.LabEnvironment)
            .WithMany(x => x.ScenarioDefinitions)
            .HasForeignKey(x => x.LabEnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.LabEnvironmentId, x.ScenarioKey }).IsUnique();
    }
}

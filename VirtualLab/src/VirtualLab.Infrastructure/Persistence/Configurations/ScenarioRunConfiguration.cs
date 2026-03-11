using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class ScenarioRunConfiguration : IEntityTypeConfiguration<ScenarioRun>
{
    public void Configure(EntityTypeBuilder<ScenarioRun> builder)
    {
        builder.ToTable("ScenarioRuns");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.ReplaySignature).HasMaxLength(128);
        builder.Property(x => x.InputSnapshotJson).HasColumnType("TEXT");
        builder.Property(x => x.ResultSummaryJson).HasColumnType("TEXT");

        builder.HasOne(x => x.Site)
            .WithMany(x => x.ScenarioRuns)
            .HasForeignKey(x => x.SiteId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.ScenarioDefinition)
            .WithMany(x => x.Runs)
            .HasForeignKey(x => x.ScenarioDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.SiteId, x.StartedAtUtc });
        builder.HasIndex(x => x.ReplaySignature);
    }
}

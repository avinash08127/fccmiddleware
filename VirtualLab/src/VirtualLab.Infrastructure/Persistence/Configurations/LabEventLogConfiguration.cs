using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class LabEventLogConfiguration : IEntityTypeConfiguration<LabEventLog>
{
    public void Configure(EntityTypeBuilder<LabEventLog> builder)
    {
        builder.ToTable("LabEventLogs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.Severity).HasMaxLength(32);
        builder.Property(x => x.Category).HasMaxLength(64);
        builder.Property(x => x.EventType).HasMaxLength(64);
        builder.Property(x => x.Message).HasMaxLength(2048);
        builder.Property(x => x.RawPayloadJson).HasColumnType("TEXT");
        builder.Property(x => x.CanonicalPayloadJson).HasColumnType("TEXT");
        builder.Property(x => x.MetadataJson).HasColumnType("TEXT");

        builder.HasOne(x => x.Site)
            .WithMany(x => x.EventLogs)
            .HasForeignKey(x => x.SiteId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.FccSimulatorProfile)
            .WithMany()
            .HasForeignKey(x => x.FccSimulatorProfileId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.PreAuthSession)
            .WithMany()
            .HasForeignKey(x => x.PreAuthSessionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.SimulatedTransaction)
            .WithMany()
            .HasForeignKey(x => x.SimulatedTransactionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.ScenarioRun)
            .WithMany(x => x.EventLogs)
            .HasForeignKey(x => x.ScenarioRunId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => new { x.SiteId, x.Category, x.OccurredAtUtc });
        builder.HasIndex(x => new { x.Category, x.OccurredAtUtc });
        builder.HasIndex(x => new { x.CorrelationId, x.OccurredAtUtc });
    }
}

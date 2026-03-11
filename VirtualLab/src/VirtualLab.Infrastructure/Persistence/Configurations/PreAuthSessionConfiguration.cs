using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class PreAuthSessionConfiguration : IEntityTypeConfiguration<PreAuthSession>
{
    public void Configure(EntityTypeBuilder<PreAuthSession> builder)
    {
        builder.ToTable("PreAuthSessions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.ExternalReference).HasMaxLength(128);
        builder.Property(x => x.ReservedAmount).HasPrecision(18, 2);
        builder.Property(x => x.AuthorizedAmount).HasPrecision(18, 2);
        builder.Property(x => x.FinalAmount).HasPrecision(18, 2);
        builder.Property(x => x.FinalVolume).HasPrecision(18, 3);
        builder.Property(x => x.RawRequestJson).HasColumnType("TEXT");
        builder.Property(x => x.CanonicalRequestJson).HasColumnType("TEXT");
        builder.Property(x => x.RawResponseJson).HasColumnType("TEXT");
        builder.Property(x => x.CanonicalResponseJson).HasColumnType("TEXT");
        builder.Property(x => x.TimelineJson).HasColumnType("TEXT");

        builder.HasOne(x => x.Site)
            .WithMany(x => x.PreAuthSessions)
            .HasForeignKey(x => x.SiteId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Pump)
            .WithMany()
            .HasForeignKey(x => x.PumpId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Nozzle)
            .WithMany()
            .HasForeignKey(x => x.NozzleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.ScenarioRun)
            .WithMany(x => x.PreAuthSessions)
            .HasForeignKey(x => x.ScenarioRunId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => new { x.SiteId, x.CreatedAtUtc });
        builder.HasIndex(x => x.CorrelationId);
    }
}

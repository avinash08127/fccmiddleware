using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class SimulatedTransactionConfiguration : IEntityTypeConfiguration<SimulatedTransaction>
{
    public void Configure(EntityTypeBuilder<SimulatedTransaction> builder)
    {
        builder.ToTable("SimulatedTransactions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.ExternalTransactionId).HasMaxLength(128);
        builder.Property(x => x.Volume).HasPrecision(18, 3);
        builder.Property(x => x.UnitPrice).HasPrecision(18, 3);
        builder.Property(x => x.TotalAmount).HasPrecision(18, 2);
        builder.Property(x => x.RawPayloadJson).HasColumnType("TEXT");
        builder.Property(x => x.CanonicalPayloadJson).HasColumnType("TEXT");
        builder.Property(x => x.RawHeadersJson).HasColumnType("TEXT");
        builder.Property(x => x.DeliveryCursor).HasMaxLength(128);
        builder.Property(x => x.MetadataJson).HasColumnType("TEXT");
        builder.Property(x => x.TimelineJson).HasColumnType("TEXT");

        builder.HasOne(x => x.Site)
            .WithMany(x => x.Transactions)
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

        builder.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.PreAuthSession)
            .WithMany(x => x.Transactions)
            .HasForeignKey(x => x.PreAuthSessionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.ScenarioRun)
            .WithMany(x => x.Transactions)
            .HasForeignKey(x => x.ScenarioRunId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => new { x.SiteId, x.OccurredAtUtc });
        builder.HasIndex(x => new { x.SiteId, x.Status, x.OccurredAtUtc });
        builder.HasIndex(x => x.CorrelationId);
        builder.HasIndex(x => x.ExternalTransactionId);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence.Configurations;

internal sealed class CallbackAttemptConfiguration : IEntityTypeConfiguration<CallbackAttempt>
{
    public void Configure(EntityTypeBuilder<CallbackAttempt> builder)
    {
        builder.ToTable("CallbackAttempts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.RequestHeadersJson).HasColumnType("TEXT");
        builder.Property(x => x.RequestPayloadJson).HasColumnType("TEXT");
        builder.Property(x => x.ResponseHeadersJson).HasColumnType("TEXT");
        builder.Property(x => x.ResponsePayloadJson).HasColumnType("TEXT");
        builder.Property(x => x.ErrorMessage).HasMaxLength(2048);

        builder.HasOne(x => x.CallbackTarget)
            .WithMany(x => x.CallbackAttempts)
            .HasForeignKey(x => x.CallbackTargetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.SimulatedTransaction)
            .WithMany(x => x.CallbackAttempts)
            .HasForeignKey(x => x.SimulatedTransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.CallbackTargetId, x.AttemptedAtUtc });
        builder.HasIndex(x => new { x.SimulatedTransactionId, x.AttemptNumber }).IsUnique();
        builder.HasIndex(x => x.CorrelationId);
    }
}

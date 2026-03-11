using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(e => e.Id);

        // bigint GENERATED ALWAYS AS IDENTITY — sequential for ordered processing by the outbox publisher.
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .UseIdentityAlwaysColumn();

        builder.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(100).IsRequired();

        // Payload is stored as jsonb — column type declared explicitly for Npgsql.
        builder.Property(e => e.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(e => e.CorrelationId).HasColumnName("correlation_id").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.ProcessedAt).HasColumnName("processed_at");

        // Outbox publisher scan: find unprocessed messages in insertion order (id ASC).
        builder.HasIndex(e => e.Id)
            .HasDatabaseName("ix_outbox_unprocessed")
            .HasFilter("processed_at IS NULL");
    }
}

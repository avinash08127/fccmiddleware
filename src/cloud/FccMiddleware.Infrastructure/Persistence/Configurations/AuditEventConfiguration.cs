using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");

        // Composite PK: (Id, CreatedAt) — required for PostgreSQL range partitioning on created_at.
        builder.HasKey(e => new { e.Id, e.CreatedAt });

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(100).IsRequired();
        builder.Property(e => e.CorrelationId).HasColumnName("correlation_id").IsRequired();
        builder.Property(e => e.SiteCode).HasColumnName("site_code").HasMaxLength(50);
        builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(100).IsRequired();

        // Payload is stored as jsonb — column type declared explicitly for Npgsql.
        builder.Property(e => e.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(e => e.EntityId)
            .HasColumnName("entity_id");

        // No UpdatedAt — audit events are append-only and immutable.

        // Trace lookup: find all events for a correlation ID.
        builder.HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_audit_correlation");

        // Portal audit viewer: by tenant + event type, newest first.
        builder.HasIndex(e => new { e.LegalEntityId, e.EventType, e.CreatedAt })
            .HasDatabaseName("ix_audit_type_time");

        // Entity-scoped event lookup (e.g. agent events by DeviceId), newest first.
        // Partial index — only rows where entity_id IS NOT NULL are indexed.
        builder.HasIndex(e => new { e.EntityId, e.CreatedAt })
            .HasDatabaseName("ix_audit_entity_time")
            .HasFilter("entity_id IS NOT NULL");
    }
}

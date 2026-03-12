using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class DeadLetterItemConfiguration : IEntityTypeConfiguration<DeadLetterItem>
{
    public void Configure(EntityTypeBuilder<DeadLetterItem> builder)
    {
        builder.ToTable("dead_letter_items");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.SiteCode).HasColumnName("site_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.Type)
            .HasColumnName("type")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(e => e.FccTransactionId).HasColumnName("fcc_transaction_id").HasMaxLength(200);
        builder.Property(e => e.RawPayloadRef).HasColumnName("raw_payload_ref").HasMaxLength(500);
        builder.Property(e => e.RawPayloadJson)
            .HasColumnName("raw_payload_json")
            .HasColumnType("jsonb");
        builder.Property(e => e.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(40)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(e => e.ErrorCode).HasColumnName("error_code").HasMaxLength(100).IsRequired();
        builder.Property(e => e.ErrorMessage).HasColumnName("error_message").HasMaxLength(4000).IsRequired();
        builder.Property(e => e.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(e => e.RetryCount).HasColumnName("retry_count").HasDefaultValue(0).IsRequired();
        builder.Property(e => e.LastRetryAt).HasColumnName("last_retry_at");
        builder.Property(e => e.RetryHistoryJson)
            .HasColumnName("retry_history_json")
            .HasColumnType("jsonb");
        builder.Property(e => e.DiscardReason).HasColumnName("discard_reason").HasMaxLength(2000);
        builder.Property(e => e.DiscardedBy).HasColumnName("discarded_by").HasMaxLength(200);
        builder.Property(e => e.DiscardedAt).HasColumnName("discarded_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasIndex(e => new { e.LegalEntityId, e.Status, e.CreatedAt })
            .HasDatabaseName("ix_dead_letter_items_legal_entity_status_created_at");
    }
}

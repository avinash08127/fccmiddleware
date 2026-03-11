using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class ReconciliationRecordConfiguration : IEntityTypeConfiguration<ReconciliationRecord>
{
    public void Configure(EntityTypeBuilder<ReconciliationRecord> builder)
    {
        builder.ToTable("reconciliation_records");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.SiteCode).HasColumnName("site_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.TransactionId).HasColumnName("transaction_id").IsRequired();
        builder.Property(e => e.PreAuthId).HasColumnName("pre_auth_id");
        builder.Property(e => e.OdooOrderId).HasColumnName("odoo_order_id").HasMaxLength(200);
        builder.Property(e => e.PumpNumber).HasColumnName("pump_number").IsRequired();
        builder.Property(e => e.NozzleNumber).HasColumnName("nozzle_number").IsRequired();
        builder.Property(e => e.AuthorizedAmountMinorUnits).HasColumnName("authorized_amount_minor_units");
        builder.Property(e => e.ActualAmountMinorUnits).HasColumnName("actual_amount_minor_units").IsRequired();
        builder.Property(e => e.VarianceMinorUnits).HasColumnName("variance_minor_units");
        builder.Property(e => e.AbsoluteVarianceMinorUnits).HasColumnName("absolute_variance_minor_units");
        builder.Property(e => e.VariancePercent).HasColumnName("variance_percent").HasPrecision(9, 4);
        builder.Property(e => e.WithinTolerance).HasColumnName("within_tolerance");
        builder.Property(e => e.MatchMethod).HasColumnName("match_method").HasMaxLength(40).IsRequired();
        builder.Property(e => e.Status)
            .HasColumnName("status")
            .HasMaxLength(30)
            .HasConversion<string>()
            .HasDefaultValue(ReconciliationStatus.UNMATCHED)
            .IsRequired();
        builder.Property(e => e.AmbiguityFlag).HasColumnName("ambiguity_flag").HasDefaultValue(false).IsRequired();
        builder.Property(e => e.AmbiguityReason).HasColumnName("ambiguity_reason").HasMaxLength(200);
        builder.Property(e => e.LastMatchAttemptAt).HasColumnName("last_match_attempt_at").IsRequired();
        builder.Property(e => e.MatchedAt).HasColumnName("matched_at");
        builder.Property(e => e.ReviewedByUserId).HasColumnName("reviewed_by_user_id").HasMaxLength(200);
        builder.Property(e => e.ReviewedAtUtc).HasColumnName("reviewed_at_utc");
        builder.Property(e => e.ReviewReason).HasColumnName("review_reason").HasMaxLength(1000);
        builder.Property(e => e.EscalatedAtUtc).HasColumnName("escalated_at_utc");
        builder.Property(e => e.SchemaVersion).HasColumnName("schema_version").HasDefaultValue(1).IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasIndex(e => e.TransactionId)
            .IsUnique()
            .HasDatabaseName("ix_reconciliation_transaction");

        builder.HasIndex(e => new { e.LegalEntityId, e.Status, e.CreatedAt })
            .HasDatabaseName("ix_reconciliation_tenant_status");

        builder.HasIndex(e => new { e.SiteCode, e.Status, e.LastMatchAttemptAt })
            .HasDatabaseName("ix_reconciliation_retry")
            .HasFilter("status = 'UNMATCHED'");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "chk_reconciliation_status",
                "status IN ('UNMATCHED','MATCHED','VARIANCE_WITHIN_TOLERANCE','VARIANCE_FLAGGED','APPROVED','REJECTED')");
        });
    }
}

using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("transactions");

        // Composite PK: (Id, CreatedAt) — required for PostgreSQL range partitioning on created_at.
        builder.HasKey(e => new { e.Id, e.CreatedAt });

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.FccTransactionId).HasColumnName("fcc_transaction_id").HasMaxLength(200).IsRequired();
        builder.Property(e => e.SiteCode).HasColumnName("site_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.PumpNumber).HasColumnName("pump_number").IsRequired();
        builder.Property(e => e.NozzleNumber).HasColumnName("nozzle_number").IsRequired();
        builder.Property(e => e.ProductCode).HasColumnName("product_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.VolumeMicrolitres).HasColumnName("volume_microlitres").IsRequired();
        builder.Property(e => e.AmountMinorUnits).HasColumnName("amount_minor_units").IsRequired();
        builder.Property(e => e.UnitPriceMinorPerLitre).HasColumnName("unit_price_minor_per_litre").IsRequired();
        builder.Property(e => e.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
        builder.Property(e => e.StartedAt).HasColumnName("started_at").IsRequired();
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at").IsRequired();
        builder.Property(e => e.FiscalReceiptNumber).HasColumnName("fiscal_receipt_number").HasMaxLength(200);
        builder.Property(e => e.FccCorrelationId).HasColumnName("fcc_correlation_id").HasMaxLength(200);
        builder.Property(e => e.FccVendor)
            .HasColumnName("fcc_vendor")
            .HasMaxLength(30)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(e => e.AttendantId).HasColumnName("attendant_id").HasMaxLength(100);
        builder.Property(e => e.Status)
            .HasColumnName("status")
            .HasMaxLength(30)
            .HasConversion<string>()
            .HasDefaultValue(TransactionStatus.PENDING)
            .IsRequired();
        builder.Property(e => e.IngestionSource)
            .HasColumnName("ingestion_source")
            .HasMaxLength(30)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(e => e.RawPayloadRef).HasColumnName("raw_payload_ref").HasMaxLength(500);
        builder.Property(e => e.OdooOrderId).HasColumnName("odoo_order_id").HasMaxLength(200);
        builder.Property(e => e.SyncedToOdooAt).HasColumnName("synced_to_odoo_at");
        builder.Property(e => e.PreAuthId).HasColumnName("pre_auth_id");
        builder.Property(e => e.ReconciliationStatus)
            .HasColumnName("reconciliation_status")
            .HasMaxLength(30)
            .HasConversion<string?>();
        builder.Property(e => e.IsDuplicate).HasColumnName("is_duplicate").HasDefaultValue(false);
        builder.Property(e => e.DuplicateOfId).HasColumnName("duplicate_of_id");
        builder.Property(e => e.IsStale).HasColumnName("is_stale").HasDefaultValue(false);
        builder.Property(e => e.CorrelationId).HasColumnName("correlation_id").IsRequired();
        builder.Property(e => e.SchemaVersion).HasColumnName("schema_version").HasDefaultValue(1).IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        // Dedup unique index — the primary dedup lookup path.
        builder.HasIndex(e => new { e.FccTransactionId, e.SiteCode })
            .IsUnique()
            .HasDatabaseName("ix_transactions_dedup");

        // Odoo poll: PENDING transactions by legal entity, ordered by ingestion time.
        builder.HasIndex(e => new { e.LegalEntityId, e.Status, e.CreatedAt })
            .HasDatabaseName("ix_transactions_odoo_poll")
            .HasFilter("status = 'PENDING'");

        // Portal transaction browser: by tenant + site, newest first.
        builder.HasIndex(e => new { e.LegalEntityId, e.SiteCode, e.CreatedAt })
            .HasDatabaseName("ix_transactions_portal_search");

        // Reconciliation: find unmatched dispenses at a pump within a time window.
        builder.HasIndex(e => new { e.SiteCode, e.PumpNumber, e.CompletedAt })
            .HasDatabaseName("ix_transactions_reconciliation")
            .HasFilter("pre_auth_id IS NULL AND status = 'PENDING'");

        // Stale detection worker.
        builder.HasIndex(e => new { e.Status, e.IsStale, e.CreatedAt })
            .HasDatabaseName("ix_transactions_stale")
            .HasFilter("status = 'PENDING' AND is_stale = false");

        // CHECK constraints — EF Core validates at model level but PostgreSQL enforces in DB.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("chk_transactions_volume", "volume_microlitres > 0");
            t.HasCheckConstraint("chk_transactions_amount", "amount_minor_units > 0");
            t.HasCheckConstraint("chk_transactions_price", "unit_price_minor_per_litre > 0");
            t.HasCheckConstraint("chk_transactions_times", "completed_at >= started_at");
            t.HasCheckConstraint("chk_transactions_status", "status IN ('PENDING','SYNCED_TO_ODOO','DUPLICATE','ARCHIVED')");
        });
    }
}

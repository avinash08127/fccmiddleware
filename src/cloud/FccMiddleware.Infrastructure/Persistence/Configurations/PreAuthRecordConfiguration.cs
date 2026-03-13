using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class PreAuthRecordConfiguration : IEntityTypeConfiguration<PreAuthRecord>
{
    public void Configure(EntityTypeBuilder<PreAuthRecord> builder)
    {
        builder.ToTable("pre_auth_records");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.SiteCode).HasColumnName("site_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.OdooOrderId).HasColumnName("odoo_order_id").HasMaxLength(200).IsRequired();
        builder.Property(e => e.PumpNumber).HasColumnName("pump_number").IsRequired();
        builder.Property(e => e.NozzleNumber).HasColumnName("nozzle_number").IsRequired();
        builder.Property(e => e.ProductCode).HasColumnName("product_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
        builder.Property(e => e.RequestedAmountMinorUnits).HasColumnName("requested_amount_minor_units").IsRequired();
        builder.Property(e => e.UnitPriceMinorPerLitre).HasColumnName("unit_price_minor_per_litre").IsRequired();
        builder.Property(e => e.AuthorizedAmountMinorUnits).HasColumnName("authorized_amount_minor_units");
        builder.Property(e => e.ActualAmountMinorUnits).HasColumnName("actual_amount_minor_units");
        builder.Property(e => e.ActualVolumeMillilitres).HasColumnName("actual_volume_millilitres");
        builder.Property(e => e.AmountVarianceMinorUnits).HasColumnName("amount_variance_minor_units");
        builder.Property(e => e.VarianceBps).HasColumnName("variance_bps");
        builder.Property(e => e.Status)
            .HasColumnName("status")
            .HasMaxLength(30)
            .HasConversion<string>()
            .HasDefaultValue(PreAuthStatus.PENDING)
            .IsRequired();
        builder.Property(e => e.FccCorrelationId).HasColumnName("fcc_correlation_id").HasMaxLength(200);
        builder.Property(e => e.FccAuthorizationCode).HasColumnName("fcc_authorization_code").HasMaxLength(200);
        builder.Property(e => e.FailureReason).HasColumnName("failure_reason").HasMaxLength(500);
        builder.Property(e => e.VehicleNumber).HasColumnName("vehicle_number").HasMaxLength(50);
        builder.Property(e => e.CustomerName).HasColumnName("customer_name").HasMaxLength(200);
        builder.Property(e => e.CustomerTaxId).HasColumnName("customer_tax_id").HasMaxLength(100);  // Sensitive
        builder.Property(e => e.CustomerBusinessName).HasColumnName("customer_business_name").HasMaxLength(200);
        builder.Property(e => e.AttendantId).HasColumnName("attendant_id").HasMaxLength(100);
        builder.Property(e => e.RequestedAt).HasColumnName("requested_at").IsRequired();
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(e => e.AuthorizedAt).HasColumnName("authorized_at");
        builder.Property(e => e.DispensingAt).HasColumnName("dispensing_at");
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");
        builder.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
        builder.Property(e => e.ExpiredAt).HasColumnName("expired_at");
        builder.Property(e => e.FailedAt).HasColumnName("failed_at");
        builder.Property(e => e.MatchedFccTransactionId).HasColumnName("matched_fcc_transaction_id").HasMaxLength(256);
        builder.Property(e => e.MatchedTransactionId).HasColumnName("matched_transaction_id");
        builder.Property(e => e.SchemaVersion).HasColumnName("schema_version").HasDefaultValue(1).IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(e => e.LegalEntity)
            .WithMany()
            .HasForeignKey(e => e.LegalEntityId)
            .HasConstraintName("fk_preauth_legal_entity");

        // Idempotency key — prevents duplicate pre-auth creation while a non-terminal record exists.
        // Terminal statuses (COMPLETED, CANCELLED, EXPIRED, FAILED) allow re-request with same key.
        builder.HasIndex(e => new { e.OdooOrderId, e.SiteCode })
            .IsUnique()
            .HasDatabaseName("ix_preauth_idemp")
            .HasFilter("status IN ('PENDING','AUTHORIZED','DISPENSING')");

        // Reconciliation: match incoming dispenses to pre-auths via FCC-issued correlation ID.
        builder.HasIndex(e => e.FccCorrelationId)
            .HasDatabaseName("ix_preauth_correlation")
            .HasFilter("fcc_correlation_id IS NOT NULL");

        // Expiry worker: find active pre-auths approaching expiry.
        builder.HasIndex(e => new { e.Status, e.ExpiresAt })
            .HasDatabaseName("ix_preauth_expiry")
            .HasFilter("status IN ('PENDING','AUTHORIZED','DISPENSING')");

        // Portal pre-auth browser: by tenant + status, newest first.
        builder.HasIndex(e => new { e.LegalEntityId, e.Status, e.RequestedAt })
            .HasDatabaseName("ix_preauth_tenant_status");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("chk_preauth_amount", "requested_amount_minor_units > 0");
            t.HasCheckConstraint("chk_preauth_pump", "pump_number > 0");
            t.HasCheckConstraint("chk_preauth_nozzle", "nozzle_number > 0");
            t.HasCheckConstraint("chk_preauth_status",
                "status IN ('PENDING','AUTHORIZED','DISPENSING','COMPLETED','CANCELLED','EXPIRED','FAILED')");
        });
    }
}

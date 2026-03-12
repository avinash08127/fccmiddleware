using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class LegalEntityConfiguration : IEntityTypeConfiguration<LegalEntity>
{
    // Deterministic seed GUIDs for the initial 5 legal entities.
    public static readonly Guid MalawiId    = new("10000000-0000-0000-0000-000000000001");
    public static readonly Guid TanzaniaId  = new("10000000-0000-0000-0000-000000000002");
    public static readonly Guid BotswanaId  = new("10000000-0000-0000-0000-000000000003");
    public static readonly Guid ZambiaId    = new("10000000-0000-0000-0000-000000000004");
    public static readonly Guid NamibiaId   = new("10000000-0000-0000-0000-000000000005");

    private static readonly DateTimeOffset _seedDate = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Configure(EntityTypeBuilder<LegalEntity> builder)
    {
        builder.ToTable("legal_entities");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.BusinessCode).HasColumnName("business_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.CountryCode).HasColumnName("country_code").HasMaxLength(3).IsRequired();
        builder.Property(e => e.CountryName).HasColumnName("country_name").HasMaxLength(100).IsRequired();
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
        builder.Property(e => e.TaxAuthorityCode).HasColumnName("tax_authority_code").HasMaxLength(50).IsRequired();
        builder.Ignore(e => e.FiscalizationRequired);
        builder.Property(e => e.DefaultFiscalizationMode)
            .HasColumnName("default_fiscalization_mode")
            .HasMaxLength(30)
            .HasConversion<string>()
            .HasDefaultValue(FiscalizationMode.NONE)
            .IsRequired();
        builder.Property(e => e.FiscalizationProvider).HasColumnName("fiscalization_provider").HasMaxLength(50);
        builder.Property(e => e.DefaultTimezone).HasColumnName("default_timezone").HasMaxLength(50).IsRequired();
        builder.Property(e => e.OdooCompanyId).HasColumnName("odoo_company_id").HasMaxLength(100).IsRequired();
        builder.Property(e => e.AmountTolerancePercent).HasColumnName("amount_tolerance_percent").HasPrecision(5, 2);
        builder.Property(e => e.AmountToleranceAbsolute).HasColumnName("amount_tolerance_absolute");
        builder.Property(e => e.TimeWindowMinutes).HasColumnName("time_window_minutes");
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.DeactivatedAt).HasColumnName("deactivated_at");
        builder.Property(e => e.SyncedAt).HasColumnName("synced_at").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasIndex(e => e.BusinessCode)
            .IsUnique()
            .HasDatabaseName("uq_legal_entities_business_code");

        builder.HasIndex(e => e.CountryCode)
            .IsUnique()
            .HasDatabaseName("uq_legal_entities_country_code");

        builder.ToTable(t => t.HasCheckConstraint(
            "chk_legal_entities_default_fiscalization_mode",
            "default_fiscalization_mode IN ('FCC_DIRECT','EXTERNAL_INTEGRATION','NONE')"));

        // NOTE: LegalEntity is the tenant root — no global query filter here.

        // Seed data: initial 5 legal entities per REQ-1 / AC-1.1.
        builder.HasData(
            new LegalEntity
            {
                Id = MalawiId,
                BusinessCode = "MW",
                CountryCode = "MW",
                CountryName = "Malawi",
                Name = "Malawi",
                CurrencyCode = "MWK",
                TaxAuthorityCode = "MRA",
                DefaultTimezone = "Africa/Blantyre",
                OdooCompanyId = "ODOO-COMPANY-MW",
                DefaultFiscalizationMode = FiscalizationMode.FCC_DIRECT,
                IsActive = true,
                SyncedAt = _seedDate,
                CreatedAt = _seedDate,
                UpdatedAt = _seedDate
            },
            new LegalEntity
            {
                Id = TanzaniaId,
                BusinessCode = "TZ",
                CountryCode = "TZ",
                CountryName = "Tanzania",
                Name = "Tanzania",
                CurrencyCode = "TZS",
                TaxAuthorityCode = "TRA",
                DefaultTimezone = "Africa/Dar_es_Salaam",
                OdooCompanyId = "ODOO-COMPANY-TZ",
                DefaultFiscalizationMode = FiscalizationMode.FCC_DIRECT,
                IsActive = true,
                SyncedAt = _seedDate,
                CreatedAt = _seedDate,
                UpdatedAt = _seedDate
            },
            new LegalEntity
            {
                Id = BotswanaId,
                BusinessCode = "BW",
                CountryCode = "BW",
                CountryName = "Botswana",
                Name = "Botswana",
                CurrencyCode = "BWP",
                TaxAuthorityCode = "BURS",
                DefaultTimezone = "Africa/Gaborone",
                OdooCompanyId = "ODOO-COMPANY-BW",
                DefaultFiscalizationMode = FiscalizationMode.NONE,
                IsActive = true,
                SyncedAt = _seedDate,
                CreatedAt = _seedDate,
                UpdatedAt = _seedDate
            },
            new LegalEntity
            {
                Id = ZambiaId,
                BusinessCode = "ZM",
                CountryCode = "ZM",
                CountryName = "Zambia",
                Name = "Zambia",
                CurrencyCode = "ZMW",
                TaxAuthorityCode = "ZRA",
                DefaultTimezone = "Africa/Lusaka",
                OdooCompanyId = "ODOO-COMPANY-ZM",
                DefaultFiscalizationMode = FiscalizationMode.NONE,
                IsActive = true,
                SyncedAt = _seedDate,
                CreatedAt = _seedDate,
                UpdatedAt = _seedDate
            },
            new LegalEntity
            {
                Id = NamibiaId,
                BusinessCode = "NA",
                CountryCode = "NA",
                CountryName = "Namibia",
                Name = "Namibia",
                CurrencyCode = "NAD",
                TaxAuthorityCode = "NamRA",
                DefaultTimezone = "Africa/Windhoek",
                OdooCompanyId = "ODOO-COMPANY-NA",
                DefaultFiscalizationMode = FiscalizationMode.NONE,
                IsActive = true,
                SyncedAt = _seedDate,
                CreatedAt = _seedDate,
                UpdatedAt = _seedDate
            }
        );
    }
}

using FccMiddleware.Domain.Entities;
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

        builder.Property(e => e.CountryCode).HasColumnName("country_code").HasMaxLength(3).IsRequired();
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
        builder.Property(e => e.TaxAuthorityCode).HasColumnName("tax_authority_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.FiscalizationRequired).HasColumnName("fiscalization_required").HasDefaultValue(false);
        builder.Property(e => e.FiscalizationProvider).HasColumnName("fiscalization_provider").HasMaxLength(50);
        builder.Property(e => e.DefaultTimezone).HasColumnName("default_timezone").HasMaxLength(50).IsRequired();
        builder.Property(e => e.AmountTolerancePercent).HasColumnName("amount_tolerance_percent").HasPrecision(5, 2);
        builder.Property(e => e.AmountToleranceAbsolute).HasColumnName("amount_tolerance_absolute");
        builder.Property(e => e.TimeWindowMinutes).HasColumnName("time_window_minutes");
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.DeactivatedAt).HasColumnName("deactivated_at");
        builder.Property(e => e.SyncedAt).HasColumnName("synced_at").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasIndex(e => e.CountryCode)
            .IsUnique()
            .HasDatabaseName("uq_legal_entities_country_code");

        // NOTE: LegalEntity is the tenant root — no global query filter here.

        // Seed data: initial 5 legal entities per REQ-1 / AC-1.1.
        builder.HasData(
            new LegalEntity
            {
                Id = MalawiId,
                CountryCode = "MW",
                Name = "Malawi",
                CurrencyCode = "MWK",
                TaxAuthorityCode = "MRA",
                DefaultTimezone = "Africa/Blantyre",
                FiscalizationRequired = true,
                IsActive = true,
                SyncedAt = _seedDate,
                CreatedAt = _seedDate,
                UpdatedAt = _seedDate
            },
            new LegalEntity
            {
                Id = TanzaniaId,
                CountryCode = "TZ",
                Name = "Tanzania",
                CurrencyCode = "TZS",
                TaxAuthorityCode = "TRA",
                DefaultTimezone = "Africa/Dar_es_Salaam",
                FiscalizationRequired = true,
                IsActive = true,
                SyncedAt = _seedDate,
                CreatedAt = _seedDate,
                UpdatedAt = _seedDate
            },
            new LegalEntity
            {
                Id = BotswanaId,
                CountryCode = "BW",
                Name = "Botswana",
                CurrencyCode = "BWP",
                TaxAuthorityCode = "BURS",
                DefaultTimezone = "Africa/Gaborone",
                FiscalizationRequired = false,
                IsActive = true,
                SyncedAt = _seedDate,
                CreatedAt = _seedDate,
                UpdatedAt = _seedDate
            },
            new LegalEntity
            {
                Id = ZambiaId,
                CountryCode = "ZM",
                Name = "Zambia",
                CurrencyCode = "ZMW",
                TaxAuthorityCode = "ZRA",
                DefaultTimezone = "Africa/Lusaka",
                FiscalizationRequired = false,
                IsActive = true,
                SyncedAt = _seedDate,
                CreatedAt = _seedDate,
                UpdatedAt = _seedDate
            },
            new LegalEntity
            {
                Id = NamibiaId,
                CountryCode = "NA",
                Name = "Namibia",
                CurrencyCode = "NAD",
                TaxAuthorityCode = "NamRA",
                DefaultTimezone = "Africa/Windhoek",
                FiscalizationRequired = false,
                IsActive = true,
                SyncedAt = _seedDate,
                CreatedAt = _seedDate,
                UpdatedAt = _seedDate
            }
        );
    }
}

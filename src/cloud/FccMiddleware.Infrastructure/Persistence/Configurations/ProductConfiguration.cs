using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    private static readonly DateTimeOffset _seedDate = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id").IsRequired();
        builder.Property(e => e.ProductCode).HasColumnName("product_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.ProductName).HasColumnName("product_name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.UnitOfMeasure).HasColumnName("unit_of_measure").HasMaxLength(20).HasDefaultValue("LITRE").IsRequired();
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.SyncedAt).HasColumnName("synced_at").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(e => e.LegalEntity)
            .WithMany(le => le.Products)
            .HasForeignKey(e => e.LegalEntityId)
            .HasConstraintName("fk_products_legal_entity");

        builder.HasIndex(e => new { e.LegalEntityId, e.ProductCode })
            .IsUnique()
            .HasDatabaseName("uq_products_entity_code");

        // Multi-tenancy global query filter — only tenant-scoped queries see products for the current tenant.
        // Filter is configured in FccMiddlewareDbContext using the injected ICurrentTenantProvider.

        // Seed data: minimal development product set per entity, per seed-data-strategy.md.
        var seedProducts = BuildSeedProducts();
        builder.HasData(seedProducts);
    }

    private static Product[] BuildSeedProducts()
    {
        var products = new List<Product>();
        var entityIds = new[]
        {
            LegalEntityConfiguration.MalawiId,
            LegalEntityConfiguration.TanzaniaId,
            LegalEntityConfiguration.BotswanaId,
            LegalEntityConfiguration.ZambiaId,
            LegalEntityConfiguration.NamibiaId
        };

        var seedCodes = new[]
        {
            ("PETROL_ULP", "Unleaded Petrol"),
            ("DIESEL_50",  "Diesel 50ppm"),
            ("DIESEL_500", "Diesel 500ppm")
        };

        // Use a deterministic ID scheme: entity index (1–5) * 100 + product index (1–3)
        // Encoded as: 20000000-0000-0000-00EE-0000000000PP where EE = entity index, PP = product index.
        for (var ei = 0; ei < entityIds.Length; ei++)
        {
            for (var pi = 0; pi < seedCodes.Length; pi++)
            {
                var idBytes = new byte[16];
                idBytes[0] = 0x20;
                idBytes[14] = (byte)(ei + 1);
                idBytes[15] = (byte)(pi + 1);
                var id = new Guid(idBytes);

                products.Add(new Product
                {
                    Id = id,
                    LegalEntityId = entityIds[ei],
                    ProductCode = seedCodes[pi].Item1,
                    ProductName = seedCodes[pi].Item2,
                    UnitOfMeasure = "LITRE",
                    IsActive = true,
                    SyncedAt = _seedDate,
                    CreatedAt = _seedDate,
                    UpdatedAt = _seedDate
                });
            }
        }

        return products.ToArray();
    }
}

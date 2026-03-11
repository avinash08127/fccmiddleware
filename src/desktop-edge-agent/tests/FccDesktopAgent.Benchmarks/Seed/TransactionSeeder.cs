using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer.Entities;

namespace FccDesktopAgent.Benchmarks.Seed;

/// <summary>
/// Generates representative synthetic datasets for benchmark harnesses.
/// 30,000 records ≈ production backlog worst-case (≈ 1 week offline at 60 tx/hr × 24 hr × 7 days).
/// </summary>
public static class TransactionSeeder
{
    private static readonly string[] ProductCodes = ["ULP91", "ULP95", "DSL", "PREM98"];
    private static readonly int[] PumpNumbers = [1, 2, 3, 4, 5, 6];
    private static readonly int[] NozzleNumbers = [1, 2];

    /// <summary>Generate <paramref name="count"/> synthetic buffered transactions in chronological order.</summary>
    public static List<BufferedTransaction> Generate(int count = 30_000)
    {
        var rng = new Random(42); // Fixed seed for reproducible benchmarks
        var baseTime = DateTimeOffset.UtcNow.AddDays(-7);
        var intervalPerRecord = TimeSpan.FromDays(7) / count;

        var result = new List<BufferedTransaction>(count);

        for (int i = 0; i < count; i++)
        {
            var completedAt = baseTime + (intervalPerRecord * i);
            var startedAt = completedAt.AddSeconds(-rng.Next(30, 120));

            result.Add(new BufferedTransaction
            {
                Id = Guid.NewGuid().ToString(),
                FccTransactionId = $"FCC-{i:D8}",
                SiteCode = "SITE-001",
                PumpNumber = PumpNumbers[i % PumpNumbers.Length],
                NozzleNumber = NozzleNumbers[i % NozzleNumbers.Length],
                ProductCode = ProductCodes[i % ProductCodes.Length],
                AmountMinorUnits = rng.NextInt64(500_00, 15000_00),
                VolumeMicrolitres = rng.NextInt64(5_000_000, 80_000_000), // 5L to 80L in µL
                UnitPriceMinorPerLitre = rng.NextInt64(180_00, 220_00),
                CurrencyCode = "ZAR",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                FccVendor = "Doms",
                IngestionSource = "EdgeUpload",
                RawPayloadJson = $"{{\"seq\":{i},\"pump\":{PumpNumbers[i % PumpNumbers.Length]}}}",
                SyncStatus = i % 10 == 0 ? SyncStatus.Uploaded : SyncStatus.Pending,
                Status = TransactionStatus.Pending,
                SchemaVersion = "1.0",
                CreatedAt = completedAt.AddSeconds(rng.Next(1, 30)),
                UpdatedAt = completedAt.AddSeconds(rng.Next(1, 30)),
                UploadAttempts = 0
            });
        }

        return result;
    }
}

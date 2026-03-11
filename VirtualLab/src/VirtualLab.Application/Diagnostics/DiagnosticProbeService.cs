using Microsoft.Extensions.Options;
using VirtualLab.Domain.Benchmarking;

namespace VirtualLab.Application.Diagnostics;

public sealed class DiagnosticProbeService
{
    private readonly BenchmarkSeedProfile _seedProfile;

    public DiagnosticProbeService(IOptions<BenchmarkSeedProfile> seedProfile)
    {
        _seedProfile = seedProfile.Value;
    }

    public DiagnosticProbeResult Run(int iterations)
    {
        int sampleCount = Math.Max(iterations, 1);
        SyntheticDataset dataset = SyntheticDataset.Create(_seedProfile);

        double dashboardP95 = Measure(sampleCount, () => dataset.MeasureDashboardSummary());
        double siteLoadP95 = Measure(sampleCount, () => dataset.MeasureSiteLoad());
        double signalrP95 = Measure(sampleCount, () => dataset.MeasureSignalRBroadcast());
        double fccP95 = Measure(sampleCount, () => dataset.MeasureFccHealth());
        double pullP95 = Measure(sampleCount, () => dataset.MeasureTransactionPull(limit: 100));

        return new DiagnosticProbeResult(
            _seedProfile.ProfileName,
            _seedProfile.ComputeReplaySignature(),
            new GuardrailThresholds(
                VirtualLabGuardrails.StartupReadyMinutes,
                VirtualLabGuardrails.DashboardLoadP95Ms,
                VirtualLabGuardrails.SignalRUpdateP95Ms,
                VirtualLabGuardrails.FccEmulatorP95Ms,
                VirtualLabGuardrails.TransactionPullP95Ms),
            new DiagnosticMeasurements(
                dashboardP95,
                siteLoadP95,
                signalrP95,
                fccP95,
                pullP95,
                sampleCount));
    }

    private static double Measure(int iterations, Action action)
    {
        List<double> samples = new(iterations);

        for (int index = 0; index < iterations; index++)
        {
            var startedAt = TimeProvider.System.GetTimestamp();
            action();
            samples.Add(TimeProvider.System.GetElapsedTime(startedAt).TotalMilliseconds);
        }

        return Percentile(samples, 0.95);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        double[] ordered = values.OrderBy(value => value).ToArray();
        int index = Math.Clamp((int)Math.Ceiling((ordered.Length * percentile)) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private sealed class SyntheticDataset
    {
        private readonly IReadOnlyList<SyntheticTransaction> _transactions;
        private readonly IReadOnlyList<SyntheticSite> _sites;

        private SyntheticDataset(IReadOnlyList<SyntheticSite> sites, IReadOnlyList<SyntheticTransaction> transactions)
        {
            _sites = sites;
            _transactions = transactions;
        }

        public static SyntheticDataset Create(BenchmarkSeedProfile seedProfile)
        {
            List<SyntheticSite> sites = new(seedProfile.Sites);
            List<SyntheticTransaction> transactions = new(seedProfile.Transactions);
            Random random = new(seedProfile.ScenarioSeed);

            for (int siteIndex = 0; siteIndex < seedProfile.Sites; siteIndex++)
            {
                string siteCode = $"VL-{siteIndex + 1:D2}";
                sites.Add(new SyntheticSite(siteCode, seedProfile.PumpsPerSite, seedProfile.NozzlesPerPump));
            }

            for (int transactionIndex = 0; transactionIndex < seedProfile.Transactions; transactionIndex++)
            {
                int siteIndex = transactionIndex % seedProfile.Sites;
                int pumpNumber = (transactionIndex % seedProfile.PumpsPerSite) + 1;
                int nozzleNumber = (transactionIndex % seedProfile.NozzlesPerPump) + 1;
                decimal amount = decimal.Round((decimal)(random.NextDouble() * 2500 + 200), 2);
                DateTimeOffset occurredAt = DateTimeOffset.UtcNow.AddSeconds(-transactionIndex);

                transactions.Add(new SyntheticTransaction(
                    $"TX-{transactionIndex:D5}",
                    sites[siteIndex].SiteCode,
                    pumpNumber,
                    nozzleNumber,
                    amount,
                    occurredAt));
            }

            return new SyntheticDataset(sites, transactions);
        }

        public void MeasureDashboardSummary()
        {
            _ = _transactions
                .GroupBy(transaction => transaction.SiteCode)
                .Select(group => new
                {
                    group.Key,
                    TotalAmount = group.Sum(item => item.Amount),
                    TotalTransactions = group.Count(),
                })
                .OrderBy(item => item.Key)
                .ToArray();
        }

        public void MeasureSiteLoad()
        {
            _ = _sites
                .Select(site => new
                {
                    site.SiteCode,
                    site.PumpsPerSite,
                    site.NozzlesPerPump,
                    RecentTransactions = _transactions.Count(transaction => transaction.SiteCode == site.SiteCode),
                })
                .ToArray();
        }

        public void MeasureSignalRBroadcast()
        {
            _ = _sites
                .Select(site => $"{site.SiteCode}:{site.PumpsPerSite}:{site.NozzlesPerPump}")
                .ToArray();
        }

        public void MeasureFccHealth()
        {
            _ = _sites
                .Select(site => new
                {
                    site.SiteCode,
                    IsHealthy = true,
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                })
                .ToArray();
        }

        public void MeasureTransactionPull(int limit)
        {
            _ = _transactions
                .OrderByDescending(transaction => transaction.OccurredAt)
                .Take(limit)
                .ToArray();
        }
    }

    private sealed record SyntheticSite(string SiteCode, int PumpsPerSite, int NozzlesPerPump);

    private sealed record SyntheticTransaction(
        string TransactionId,
        string SiteCode,
        int PumpNumber,
        int NozzleNumber,
        decimal Amount,
        DateTimeOffset OccurredAt);
}

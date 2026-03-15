using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VirtualLab.Api.Hubs;
using VirtualLab.Application.Diagnostics;
using VirtualLab.Application.Forecourt;
using VirtualLab.Application.Management;
using VirtualLab.Domain.Benchmarking;
using VirtualLab.Domain.Enums;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Api.Diagnostics;

public sealed class ApiDiagnosticProbeService(
    IOptions<BenchmarkSeedProfile> seedProfile,
    VirtualLabDbContext dbContext,
    IVirtualLabManagementService managementService,
    IForecourtSimulationService forecourtSimulationService,
    IHubContext<LabLiveHub> hubContext)
{
    private readonly BenchmarkSeedProfile _seedProfile = seedProfile.Value;

    public async Task<DiagnosticProbeResult> RunAsync(int iterations, CancellationToken cancellationToken = default)
    {
        int sampleCount = Math.Max(iterations, 1);
        string siteCode = await ResolveBenchmarkSiteCodeAsync(cancellationToken);

        double dashboardP95 = await MeasureAsync(sampleCount, ExecuteDashboardHotPathAsync, cancellationToken);
        double siteLoadP95 = await MeasureAsync(
            sampleCount,
            async ct => _ = await managementService.ListSitesAsync(true, ct),
            cancellationToken);
        double signalrP95 = await MeasureAsync(sampleCount, BroadcastSignalRAsync, cancellationToken);
        double fccP95 = await MeasureAsync(
            sampleCount,
            async ct => _ = await forecourtSimulationService.GetHealthAsync(siteCode, ct),
            cancellationToken);
        double pullP95 = await MeasureAsync(
            sampleCount,
            async ct => _ = await forecourtSimulationService.PullTransactionsAsync(siteCode, 100, null, ct),
            cancellationToken);

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

    private async Task ExecuteDashboardHotPathAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset sinceUtc = now.AddHours(-24);

        _ = await managementService.ListSitesAsync(true, cancellationToken);

        SimulatedTransactionStatus[] activeStatuses =
        [
            SimulatedTransactionStatus.Created,
            SimulatedTransactionStatus.ReadyForDelivery,
            SimulatedTransactionStatus.Delivered,
        ];

        _ = await dbContext.SimulatedTransactions
            .AsNoTracking()
            .CountAsync(x => activeStatuses.Contains(x.Status), cancellationToken);

        _ = await dbContext.SimulatedTransactions
            .AsNoTracking()
            .Where(x => activeStatuses.Contains(x.Status))
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(8)
            .Select(x => new
            {
                SiteCode = x.Site.SiteCode,
                x.CorrelationId,
                x.ExternalTransactionId,
                x.Status,
                x.DeliveryMode,
                PumpNumber = x.Pump.PumpNumber,
                NozzleNumber = x.Nozzle.NozzleNumber,
                ProductCode = x.Product.ProductCode,
                x.Volume,
                x.TotalAmount,
            })
            .ToListAsync(cancellationToken);

        _ = await dbContext.LabEventLogs
            .AsNoTracking()
            .CountAsync(x => x.Category == "AuthFailure" && x.OccurredAtUtc >= sinceUtc, cancellationToken);

        _ = await dbContext.LabEventLogs
            .AsNoTracking()
            .Where(x => x.Category == "AuthFailure")
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(6)
            .Select(x => new
            {
                SiteCode = x.Site != null ? x.Site.SiteCode : null,
                x.EventType,
                x.Message,
                x.CorrelationId,
            })
            .ToListAsync(cancellationToken);

        _ = await dbContext.CallbackAttempts
            .AsNoTracking()
            .CountAsync(x => x.AttemptedAtUtc >= sinceUtc && x.Status == CallbackAttemptStatus.Succeeded, cancellationToken);

        _ = await dbContext.CallbackAttempts
            .AsNoTracking()
            .CountAsync(x => x.AttemptedAtUtc >= sinceUtc && x.Status == CallbackAttemptStatus.Failed, cancellationToken);

        _ = await dbContext.CallbackAttempts
            .AsNoTracking()
            .CountAsync(
                x => x.AttemptedAtUtc >= sinceUtc &&
                     (x.Status == CallbackAttemptStatus.Pending || x.Status == CallbackAttemptStatus.InProgress),
                cancellationToken);

        _ = await dbContext.CallbackAttempts
            .AsNoTracking()
            .OrderByDescending(x => x.AttemptedAtUtc)
            .Take(6)
            .Select(x => new
            {
                SiteCode = x.SimulatedTransaction.Site.SiteCode,
                TargetKey = x.CallbackTarget.TargetKey,
                x.CorrelationId,
                x.AttemptNumber,
                x.Status,
                x.ResponseStatusCode,
            })
            .ToListAsync(cancellationToken);
    }

    private async Task BroadcastSignalRAsync(CancellationToken cancellationToken)
    {
        await hubContext.Clients.All.SendAsync(
            "lab-event",
            new
            {
                eventType = "diagnostic-probe",
                occurredAtUtc = DateTimeOffset.UtcNow,
                correlationId = $"probe-{Guid.NewGuid():N}",
            },
            cancellationToken);
    }

    private async Task<string> ResolveBenchmarkSiteCodeAsync(CancellationToken cancellationToken)
    {
        string? siteCode = await dbContext.Sites
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SiteCode)
            .Select(x => x.SiteCode)
            .FirstOrDefaultAsync(cancellationToken);

        return siteCode ?? throw new InvalidOperationException("The benchmark probe requires at least one active site.");
    }

    private static async Task<double> MeasureAsync(
        int iterations,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        List<double> samples = new(iterations);

        for (int index = 0; index < iterations; index++)
        {
            long startedAt = TimeProvider.System.GetTimestamp();
            await action(cancellationToken);
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
}

using System.Diagnostics;
using System.Diagnostics.Metrics;
using VirtualLab.Application.Observability;

namespace VirtualLab.Infrastructure.Observability;

public sealed class VirtualLabTelemetry : IVirtualLabTelemetry
{
    private static readonly ActivitySource ActivitySource = new("VirtualLab.Management");
    private static readonly Meter Meter = new("VirtualLab.Management", "1.0.0");

    private readonly Counter<long> environmentExportCounter = Meter.CreateCounter<long>("virtual_lab.environment.exports");
    private readonly Counter<long> environmentImportCounter = Meter.CreateCounter<long>("virtual_lab.environment.imports");
    private readonly Counter<long> pruneTransactionCounter = Meter.CreateCounter<long>("virtual_lab.prune.transactions_removed");
    private readonly Counter<long> pruneCallbackAttemptCounter = Meter.CreateCounter<long>("virtual_lab.prune.callback_attempts_removed");
    private readonly Counter<long> pruneLogCounter = Meter.CreateCounter<long>("virtual_lab.prune.logs_removed");

    public IDisposable? StartEnvironmentOperation(string operationName, string environmentKey, bool emitActivities)
    {
        if (!emitActivities)
        {
            return null;
        }

        Activity? activity = ActivitySource.StartActivity(operationName, ActivityKind.Internal);
        activity?.SetTag("virtual_lab.environment.key", environmentKey);
        return activity;
    }

    public void RecordEnvironmentExport(string environmentKey, int siteCount, bool includeRuntimeData, bool emitMetrics)
    {
        if (!emitMetrics)
        {
            return;
        }

        environmentExportCounter.Add(
            1,
            new KeyValuePair<string, object?>("virtual_lab.environment.key", environmentKey),
            new KeyValuePair<string, object?>("virtual_lab.include_runtime_data", includeRuntimeData),
            new KeyValuePair<string, object?>("virtual_lab.site_count", siteCount));
    }

    public void RecordEnvironmentImport(string environmentKey, int siteCount, bool replaceExisting, bool emitMetrics)
    {
        if (!emitMetrics)
        {
            return;
        }

        environmentImportCounter.Add(
            1,
            new KeyValuePair<string, object?>("virtual_lab.environment.key", environmentKey),
            new KeyValuePair<string, object?>("virtual_lab.replace_existing", replaceExisting),
            new KeyValuePair<string, object?>("virtual_lab.site_count", siteCount));
    }

    public void RecordEnvironmentPrune(
        string environmentKey,
        int transactionsRemoved,
        int callbackAttemptsRemoved,
        int logsRemoved,
        bool dryRun,
        bool emitMetrics)
    {
        if (!emitMetrics)
        {
            return;
        }

        KeyValuePair<string, object?>[] tags =
        [
            new("virtual_lab.environment.key", environmentKey),
            new("virtual_lab.dry_run", dryRun),
        ];

        pruneTransactionCounter.Add(transactionsRemoved, tags);
        pruneCallbackAttemptCounter.Add(callbackAttemptsRemoved, tags);
        pruneLogCounter.Add(logsRemoved, tags);
    }
}

namespace VirtualLab.Application.Observability;

public interface IVirtualLabTelemetry
{
    IDisposable? StartEnvironmentOperation(string operationName, string environmentKey, bool emitActivities);
    void RecordEnvironmentExport(string environmentKey, int siteCount, bool includeRuntimeData, bool emitMetrics);
    void RecordEnvironmentImport(string environmentKey, int siteCount, bool replaceExisting, bool emitMetrics);
    void RecordEnvironmentPrune(
        string environmentKey,
        int transactionsRemoved,
        int callbackAttemptsRemoved,
        int logsRemoved,
        bool dryRun,
        bool emitMetrics);
}

namespace FccMiddleware.Application.Observability;

public interface IObservabilityMetrics
{
    void RecordIngestionSuccess(string source, string siteCode, string vendor, int count = 1);

    void RecordIngestionFailure(string source, string category, string siteCode, string vendor, int count = 1);

    void RecordApplicationError(string category, string route, int count = 1);

    void RecordOdooPollLatency(Guid legalEntityId, double latencyMs, int transactionCount);

    void RecordReconciliationMatchRate(Guid legalEntityId, string siteCode, string matchMethod, bool matched);

    void RecordReconciliationSkipped(Guid legalEntityId, string siteCode, string reason);

    void RecordEdgeBufferDepth(Guid legalEntityId, string siteCode, Guid deviceId, int pendingUploadCount);

    void RecordEdgeSyncLag(Guid legalEntityId, string siteCode, Guid deviceId, double syncLagHours);

    void RecordFccHeartbeatAge(Guid legalEntityId, string siteCode, Guid deviceId, double heartbeatAgeMinutes);

    void RecordStaleTransactionCount(int staleCount);

    void RecordEdgeAgentOfflineHours(Guid legalEntityId, string siteCode, Guid deviceId, double offlineHours);
}

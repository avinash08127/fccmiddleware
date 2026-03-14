using System.Text.Json;
using FccMiddleware.Application.Observability;
using Microsoft.Extensions.Hosting;

namespace FccMiddleware.Infrastructure.Observability;

public sealed class CloudWatchEmfMetricSink : IObservabilityMetrics
{
    private const string MetricNamespace = "FccMiddleware/CloudBackend";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object ConsoleSync = new();

    private readonly IHostEnvironment _environment;

    public CloudWatchEmfMetricSink(
        IHostEnvironment environment)
    {
        _environment = environment;
    }

    public void RecordIngestionSuccess(string source, string siteCode, string vendor, int count = 1) =>
        EmitMetric(
            "ingestion_success_count",
            count,
            "Count",
            ("source", source),
            ("siteCode", siteCode),
            ("vendor", vendor));

    public void RecordIngestionFailure(string source, string category, string siteCode, string vendor, int count = 1)
    {
        EmitMetric(
            "ingestion_error_count",
            count,
            "Count",
            ("source", source),
            ("category", category),
            ("siteCode", siteCode),
            ("vendor", vendor));

        RecordApplicationError(category, "/api/v1/transactions", count);
    }

    public void RecordApplicationError(string category, string route, int count = 1) =>
        EmitMetric(
            "application_error_count",
            count,
            "Count",
            ("category", category),
            ("route", route));

    public void RecordOdooPollLatency(Guid legalEntityId, double latencyMs, int transactionCount)
    {
        EmitMetric(
            "odoo_poll_latency_ms",
            latencyMs,
            "Milliseconds",
            ("legalEntityId", legalEntityId.ToString()));

        EmitMetric(
            "odoo_poll_transactions_returned",
            transactionCount,
            "Count",
            ("legalEntityId", legalEntityId.ToString()));
    }

    public void RecordReconciliationMatchRate(Guid legalEntityId, string siteCode, string matchMethod, bool matched) =>
        EmitMetric(
            "reconciliation_match_rate_percent",
            matched ? 100 : 0,
            "Percent",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("matchMethod", matchMethod));

    public void RecordReconciliationSkipped(Guid legalEntityId, string siteCode, string reason) =>
        EmitMetric(
            "reconciliation_skipped_count",
            1,
            "Count",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("reason", reason));

    public void RecordEdgeBufferDepth(Guid legalEntityId, string siteCode, Guid deviceId, int pendingUploadCount) =>
        EmitMetric(
            "edge_buffer_depth_records",
            pendingUploadCount,
            "Count",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("deviceId", deviceId.ToString()));

    public void RecordEdgeSyncLag(Guid legalEntityId, string siteCode, Guid deviceId, double syncLagHours) =>
        EmitMetric(
            "edge_sync_lag_hours",
            syncLagHours,
            "None",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("deviceId", deviceId.ToString()));

    public void RecordFccHeartbeatAge(Guid legalEntityId, string siteCode, Guid deviceId, double heartbeatAgeMinutes) =>
        EmitMetric(
            "fcc_heartbeat_age_minutes",
            heartbeatAgeMinutes,
            "None",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("deviceId", deviceId.ToString()));

    public void RecordStaleTransactionCount(int staleCount) =>
        EmitMetric("stale_transaction_count", staleCount, "Count");

    public void RecordEdgeAgentOfflineHours(Guid legalEntityId, string siteCode, Guid deviceId, double offlineHours) =>
        EmitMetric(
            "edge_agent_offline_hours",
            offlineHours,
            "None",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("deviceId", deviceId.ToString()));

    public void RecordAgentCommandCreated(Guid legalEntityId, string siteCode, Guid deviceId, string commandType) =>
        EmitMetric(
            "agent_command_created_count",
            1,
            "Count",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("deviceId", deviceId.ToString()),
            ("commandType", commandType));

    public void RecordAgentCommandAcked(Guid legalEntityId, string siteCode, Guid deviceId, string commandType) =>
        EmitMetric(
            "agent_command_acked_count",
            1,
            "Count",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("deviceId", deviceId.ToString()),
            ("commandType", commandType));

    public void RecordAgentCommandFailed(Guid legalEntityId, string siteCode, Guid deviceId, string commandType) =>
        EmitMetric(
            "agent_command_failed_count",
            1,
            "Count",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("deviceId", deviceId.ToString()),
            ("commandType", commandType));

    public void RecordAgentCommandExpired(Guid legalEntityId, string siteCode, Guid deviceId, string commandType) =>
        EmitMetric(
            "agent_command_expired_count",
            1,
            "Count",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("deviceId", deviceId.ToString()),
            ("commandType", commandType));

    public void RecordAgentPushHintAttempted(Guid legalEntityId, string siteCode, Guid deviceId, string kind) =>
        EmitMetric(
            "agent_push_hint_attempted_count",
            1,
            "Count",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("deviceId", deviceId.ToString()),
            ("kind", kind));

    public void RecordAgentPushHintSucceeded(Guid legalEntityId, string siteCode, Guid deviceId, string kind) =>
        EmitMetric(
            "agent_push_hint_succeeded_count",
            1,
            "Count",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("deviceId", deviceId.ToString()),
            ("kind", kind));

    public void RecordAgentPushHintFailed(Guid legalEntityId, string siteCode, Guid deviceId, string kind) =>
        EmitMetric(
            "agent_push_hint_failed_count",
            1,
            "Count",
            ("legalEntityId", legalEntityId.ToString()),
            ("siteCode", siteCode),
            ("deviceId", deviceId.ToString()),
            ("kind", kind));

    public void RecordBootstrapTokenHistoryApiLatency(Guid legalEntityId, double latencyMs) =>
        EmitMetric(
            "bootstrap_token_history_api_latency_ms",
            latencyMs,
            "Milliseconds",
            ("legalEntityId", legalEntityId.ToString()));

    private void EmitMetric(string name, double value, string unit, params (string Key, string Value)[] dimensions)
    {
        var dimensionKeys = new List<string> { "service", "environment" };
        var payload = new Dictionary<string, object?>
        {
            ["service"] = _environment.ApplicationName,
            ["environment"] = _environment.EnvironmentName
        };

        foreach (var (key, rawValue) in dimensions)
        {
            var valueText = Sanitize(rawValue);
            payload[key] = valueText;
            dimensionKeys.Add(key);
        }

        payload[name] = value;
        payload["_aws"] = new
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CloudWatchMetrics = new[]
            {
                new
                {
                    Namespace = MetricNamespace,
                    Dimensions = new[] { dimensionKeys.ToArray() },
                    Metrics = new[]
                    {
                        new
                        {
                            Name = name,
                            Unit = unit
                        }
                    }
                }
            }
        };

        lock (ConsoleSync)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        }
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return value.Length <= 250 ? value : value[..250];
    }
}

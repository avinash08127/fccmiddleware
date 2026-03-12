namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Reports device health telemetry to the cloud backend via
/// <c>POST /api/v1/agent/telemetry</c>.
/// Called by <see cref="Runtime.CadenceController"/> on internet-up telemetry ticks
/// (architecture rule #10: no independent timer loop).
///
/// Fire-and-forget: if the send fails, the report is skipped (no buffering of telemetry).
/// </summary>
public interface ITelemetryReporter
{
    /// <summary>
    /// Collect all telemetry data and send to cloud.
    /// Returns <c>true</c> if the report was sent successfully; <c>false</c> on failure (silently skipped).
    /// </summary>
    Task<bool> ReportAsync(CancellationToken ct);
}

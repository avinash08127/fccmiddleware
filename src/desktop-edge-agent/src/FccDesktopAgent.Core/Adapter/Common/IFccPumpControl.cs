namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Optional interface for adapters that support direct pump control commands.
/// Ported from legacy ForecourtClient: EmergencyBlock(), UnblockPump(), SoftLock(), Unlock().
///
/// Check if your IFccAdapter also implements this interface:
///   if (adapter is IFccPumpControl pumpControl) { ... }
/// </summary>
public interface IFccPumpControl
{
    /// <summary>
    /// Emergency-stop a fuelling point. Sends FpEmergencyStop_req (JPL).
    /// Equivalent to legacy ForecourtClient.EmergencyBlock().
    /// </summary>
    Task<PumpControlResult> EmergencyStopAsync(int fpId, CancellationToken ct);

    /// <summary>
    /// Cancel an emergency stop. Sends FpCancelEmergencyStop_req.
    /// Equivalent to legacy ForecourtClient.UnblockPump().
    /// </summary>
    Task<PumpControlResult> CancelEmergencyStopAsync(int fpId, CancellationToken ct);

    /// <summary>
    /// Close (soft-lock) a fuelling point. Prevents new transactions.
    /// Equivalent to legacy ForecourtClient.SoftLock().
    /// </summary>
    Task<PumpControlResult> ClosePumpAsync(int fpId, CancellationToken ct);

    /// <summary>
    /// Open (unlock) a fuelling point. Allows new transactions.
    /// Equivalent to legacy ForecourtClient.Unlock().
    /// </summary>
    Task<PumpControlResult> OpenPumpAsync(int fpId, CancellationToken ct);
}

/// <summary>Result of a pump control command.</summary>
public sealed record PumpControlResult(bool Success, string? ErrorMessage = null);

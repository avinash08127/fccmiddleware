using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Adapter.Doms.Jpl;

namespace FccDesktopAgent.Core.Adapter.Doms.Protocol;

/// <summary>
/// JPL message builders for pump control operations.
/// Ported from legacy ForecourtClient: EmergencyBlock(), UnblockPump(), SoftLock(), Unlock().
/// </summary>
internal static class DomsPumpControlHandler
{
    // ── JPL message names ────────────────────────────────────────────────────

    public const string EmergencyStopRequest = "FpEmergencyStop_req";
    public const string EmergencyStopResponse = "FpEmergencyStop_resp";
    public const string CancelEmergencyStopRequest = "FpCancelEmergencyStop_req";
    public const string CancelEmergencyStopResponse = "FpCancelEmergencyStop_resp";
    public const string CloseRequest = "FpClose_req";
    public const string CloseResponse = "FpClose_resp";
    public const string OpenRequest = "FpOpen_req";
    public const string OpenResponse = "FpOpen_resp";

    private const string ResultOk = "0";

    /// <summary>
    /// Build FpEmergencyStop_req. Legacy: EmergencyBlock(fpId).
    /// </summary>
    public static JplMessage BuildEmergencyStopRequest(int fpId)
        => new(Name: EmergencyStopRequest, Data: new Dictionary<string, string>
        {
            ["FpId"] = fpId.ToString("00"),
        });

    /// <summary>
    /// Build FpCancelEmergencyStop_req. Legacy: UnblockPump(fpId).
    /// </summary>
    public static JplMessage BuildCancelEmergencyStopRequest(int fpId)
        => new(Name: CancelEmergencyStopRequest, Data: new Dictionary<string, string>
        {
            ["FpId"] = fpId.ToString("00"),
        });

    /// <summary>
    /// Build FpClose_req. Legacy: SoftLock(fpId).
    /// </summary>
    public static JplMessage BuildCloseRequest(int fpId)
        => new(Name: CloseRequest, Data: new Dictionary<string, string>
        {
            ["FpId"] = fpId.ToString("00"),
        });

    /// <summary>
    /// Build FpOpen_req. Legacy: Unlock(fpId).
    /// </summary>
    public static JplMessage BuildOpenRequest(int fpId)
        => new(Name: OpenRequest, Data: new Dictionary<string, string>
        {
            ["FpId"] = fpId.ToString("00"),
        });

    /// <summary>
    /// Validate a pump control response. Returns success result.
    /// </summary>
    public static PumpControlResult ValidateControlResponse(JplMessage response)
    {
        var resultCode = response.Data?.TryGetValue("ResultCode", out var rc) == true ? rc : null;
        return resultCode == ResultOk
            ? new PumpControlResult(true)
            : new PumpControlResult(false, $"Pump control failed: ResultCode={resultCode ?? "missing"}");
    }
}

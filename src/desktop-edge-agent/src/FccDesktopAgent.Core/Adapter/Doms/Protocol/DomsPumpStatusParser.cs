using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Adapter.Doms.Jpl;
using FccDesktopAgent.Core.Adapter.Doms.Model;

namespace FccDesktopAgent.Core.Adapter.Doms.Protocol;

/// <summary>
/// Parses DOMS FpStatus responses into canonical PumpStatus records.
///
/// JPL messages:
///   Request:  FpStatus_req  (FpId indicates which pump or 0 for all pumps)
///   Response: FpStatus_resp (data contains pump state fields)
/// </summary>
public static class DomsPumpStatusParser
{
    /// <summary>JPL message name for pump status request.</summary>
    public const string StatusRequest = "FpStatus_req";

    /// <summary>JPL message name for pump status response.</summary>
    public const string StatusResponse = "FpStatus_resp";

    /// <summary>
    /// Build a pump status request for all configured pumps.
    /// </summary>
    /// <param name="fpId">Fuelling point ID (0 = all pumps).</param>
    /// <returns>JPL message ready to send.</returns>
    public static JplMessage BuildStatusRequest(int fpId = 0)
    {
        return new JplMessage(
            Name: StatusRequest,
            Data: new Dictionary<string, string>
            {
                ["FpId"] = fpId.ToString(),
            });
    }

    /// <summary>
    /// Parse a pump status response into canonical PumpStatus records.
    /// </summary>
    /// <param name="response">JPL response message.</param>
    /// <param name="siteCode">Site identifier for the status record.</param>
    /// <param name="currencyCode">ISO 4217 currency code.</param>
    /// <param name="pumpNumberOffset">Offset added to raw FCC pump numbers.</param>
    /// <param name="observedAtUtc">UTC timestamp for the observation.</param>
    /// <returns>List of parsed pump status records.</returns>
    public static IReadOnlyList<PumpStatus> ParseStatusResponse(
        JplMessage response,
        string siteCode,
        string currencyCode,
        int pumpNumberOffset,
        DateTimeOffset observedAtUtc)
    {
        if (response.Name != StatusResponse)
            return [];

        var data = response.Data;
        if (data is null)
            return [];

        if (!data.TryGetValue("FpId", out var fpIdStr) || !int.TryParse(fpIdStr, out var fpId))
            return [];

        if (!data.TryGetValue("FpMainState", out var mainStateStr) || !int.TryParse(mainStateStr, out var mainStateCode))
            return [];

        var canonicalState = Enum.IsDefined(typeof(DomsFpMainState), mainStateCode)
            ? ((DomsFpMainState)mainStateCode).ToCanonicalPumpState()
            : PumpState.Unknown;

        var nozzleId = data.TryGetValue("NozzleId", out var nozzleStr) && int.TryParse(nozzleStr, out var nid)
            ? nid
            : 1;

        // Extract supplemental parameters when present (FpStatus_3 extended data)
        PumpStatusSupplemental? supplemental = TryParseSupplemental(data);

        return
        [
            new PumpStatus
            {
                SiteCode = siteCode,
                PumpNumber = fpId + pumpNumberOffset,
                NozzleNumber = nozzleId,
                State = canonicalState,
                CurrencyCode = currencyCode,
                StatusSequence = 0,
                ObservedAtUtc = observedAtUtc,
                Source = PumpStatusSource.FccLive,
                FccStatusCode = mainStateCode.ToString(),
                CurrentVolumeLitres = data.TryGetValue("CurrentVolume", out var vol) ? vol : null,
                CurrentAmount = data.TryGetValue("CurrentAmount", out var amt) ? amt : null,
                UnitPrice = data.TryGetValue("UnitPrice", out var up) ? up : null,
                Supplemental = supplemental,
            }
        ];
    }

    /// <summary>
    /// Attempt to extract supplemental parameters from JPL data.
    /// Returns null if no supplemental fields are present (backwards compatible).
    /// </summary>
    private static PumpStatusSupplemental? TryParseSupplemental(IReadOnlyDictionary<string, string> data)
    {
        // Check if any supplemental fields exist
        bool hasAny = data.ContainsKey("FpAvailableGrades") ||
                      data.ContainsKey("FpAvailableStorageModules") ||
                      data.ContainsKey("FpGradeOptionNo") ||
                      data.ContainsKey("FpFuellingVolumeExt") ||
                      data.ContainsKey("FpFuellingMoneyExt") ||
                      data.ContainsKey("AttendantAccountId") ||
                      data.ContainsKey("FpBlockingStatus") ||
                      data.ContainsKey("FpOperationModeNo") ||
                      data.ContainsKey("PgId") ||
                      data.ContainsKey("NozzleTagReaderId") ||
                      data.ContainsKey("FpAlarmStatus") ||
                      data.ContainsKey("NozzleDetailId");

        if (!hasAny)
            return null;

        return new PumpStatusSupplemental
        {
            AvailableStorageModules = ParseIntList(data, "FpAvailableStorageModules"),
            AvailableGrades = ParseIntList(data, "FpAvailableGrades"),
            GradeOptionNo = ParseIntOrNull(data, "FpGradeOptionNo"),
            FuellingVolumeExtended = ParseLongOrNull(data, "FpFuellingVolumeExt"),
            FuellingMoneyExtended = ParseLongOrNull(data, "FpFuellingMoneyExt"),
            AttendantAccountId = data.TryGetValue("AttendantAccountId", out var attId) ? attId : null,
            BlockingStatus = data.TryGetValue("FpBlockingStatus", out var bs) ? bs : null,
            NozzleDetail = ParseNozzleDetail(data),
            OperationModeNo = ParseIntOrNull(data, "FpOperationModeNo"),
            PriceGroupId = ParseIntOrNull(data, "PgId"),
            NozzleTagReaderId = data.TryGetValue("NozzleTagReaderId", out var ntr) ? ntr : null,
            AlarmStatus = data.TryGetValue("FpAlarmStatus", out var alarm) ? alarm : null,
            MinPresetValues = ParseLongList(data, "FpMinPresetValues"),
        };
    }

    private static int? ParseIntOrNull(IReadOnlyDictionary<string, string> data, string key)
        => data.TryGetValue(key, out var val) && int.TryParse(val, out var result) ? result : null;

    private static long? ParseLongOrNull(IReadOnlyDictionary<string, string> data, string key)
        => data.TryGetValue(key, out var val) && long.TryParse(val, out var result) ? result : null;

    private static IReadOnlyList<int>? ParseIntList(IReadOnlyDictionary<string, string> data, string key)
    {
        if (!data.TryGetValue(key, out var val) || string.IsNullOrWhiteSpace(val))
            return null;

        var items = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<int> result = [];
        foreach (var item in items)
        {
            if (int.TryParse(item, out var parsed))
                result.Add(parsed);
        }

        return result.Count > 0 ? result : null;
    }

    private static IReadOnlyList<long>? ParseLongList(IReadOnlyDictionary<string, string> data, string key)
    {
        if (!data.TryGetValue(key, out var val) || string.IsNullOrWhiteSpace(val))
            return null;

        var items = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<long> result = [];
        foreach (var item in items)
        {
            if (long.TryParse(item, out var parsed))
                result.Add(parsed);
        }

        return result.Count > 0 ? result : null;
    }

    private static NozzleDetail? ParseNozzleDetail(IReadOnlyDictionary<string, string> data)
    {
        if (!data.TryGetValue("NozzleDetailId", out var idStr) || !int.TryParse(idStr, out var id))
            return null;

        return new NozzleDetail(
            Id: id,
            AsciiCode: data.TryGetValue("NozzleDetailAsciiCode", out var ac) ? ac : null,
            AsciiChar: data.TryGetValue("NozzleDetailAsciiChar", out var ach) ? ach : null);
    }
}

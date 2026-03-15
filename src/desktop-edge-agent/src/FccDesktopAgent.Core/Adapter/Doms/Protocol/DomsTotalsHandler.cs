using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Adapter.Doms.Jpl;
using FccDesktopAgent.Core.Adapter.Doms.Mapping;

namespace FccDesktopAgent.Core.Adapter.Doms.Protocol;

/// <summary>
/// JPL message builder for pump totals queries.
/// Ported from legacy FpTotals (EXTC 0x09): FpId, TotalVol, TotalMoney.
/// </summary>
internal static class DomsTotalsHandler
{
    public const string TotalsRequest = "FpTotals_req";
    public const string TotalsResponse = "FpTotals_resp";

    /// <summary>Build FpTotals_req. fpId=0 means all pumps.</summary>
    public static JplMessage BuildTotalsRequest(int fpId = 0)
        => new(Name: TotalsRequest, Data: new Dictionary<string, string>
        {
            ["FpId"] = fpId.ToString(),
        });

    /// <summary>
    /// Parse FpTotals_resp into PumpTotals records.
    /// Data contains: FpId, TotalVol (centilitres), TotalMoney (DOMS x10).
    /// </summary>
    public static IReadOnlyList<PumpTotals> ParseTotalsResponse(
        JplMessage response,
        string currencyCode,
        int pumpNumberOffset)
    {
        if (response.Name != TotalsResponse) return [];

        var data = response.Data;
        if (data is null) return [];

        if (!data.TryGetValue("FpId", out var fpIdStr) || !int.TryParse(fpIdStr, out var fpId))
            return [];

        var totalVolCl = data.TryGetValue("TotalVol", out var volStr) && long.TryParse(volStr, out var vol)
            ? vol : 0L;
        var totalMoneyX10 = data.TryGetValue("TotalMoney", out var monStr) && long.TryParse(monStr, out var mon)
            ? mon : 0L;

        return
        [
            new PumpTotals(
                PumpNumber: fpId + pumpNumberOffset,
                TotalVolumeMicrolitres: DomsCanonicalMapper.CentilitresToMicrolitres(totalVolCl),
                TotalAmountMinorUnits: DomsCanonicalMapper.DomsAmountToMinorUnits(totalMoneyX10),
                CurrencyCode: currencyCode,
                ObservedAtUtc: DateTimeOffset.UtcNow)
        ];
    }
}

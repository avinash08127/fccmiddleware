using System.Globalization;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Adapter.Doms.Jpl;

namespace FccDesktopAgent.Core.Adapter.Doms.Protocol;

/// <summary>
/// JPL message builders for price management operations.
/// Ported from legacy ForecourtClient: RequestFcPriceSet(), SendDynamicPriceUpdate().
/// </summary>
internal static class DomsPriceHandler
{
    public const string PriceSetRequest = "FcPriceSet_req";
    public const string PriceSetResponse = "FcPriceSet_resp";
    public const string PriceUpdateRequest = "FcPriceUpdate_req";
    public const string PriceUpdateResponse = "FcPriceUpdate_resp";

    private const string ResultOk = "0";

    /// <summary>Build FcPriceSet_req to query current prices.</summary>
    public static JplMessage BuildPriceSetRequest()
        => new(Name: PriceSetRequest);

    /// <summary>
    /// Parse FcPriceSet_resp into a PriceSetSnapshot.
    /// Legacy format: PriceSetId, GradeCount, Grade_N_Id, Grade_N_Price, ...
    /// </summary>
    public static PriceSetSnapshot? ParsePriceSetResponse(JplMessage response, string currencyCode)
    {
        if (response.Name != PriceSetResponse) return null;
        var data = response.Data;
        if (data is null) return null;

        var priceSetId = data.TryGetValue("PriceSetId", out var psId) ? psId : "unknown";
        var gradeCount = data.TryGetValue("GradeCount", out var gcStr) && int.TryParse(gcStr, out var gc) ? gc : 0;

        var priceGroupIds = data.TryGetValue("PriceGroupIds", out var pgIds)
            ? pgIds.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
            : new List<string>();

        var grades = new List<GradePrice>();
        for (var i = 0; i < gradeCount; i++)
        {
            var gradeId = data.TryGetValue($"Grade_{i}_Id", out var gId) ? gId : $"{i + 1:00}";
            var gradeName = data.TryGetValue($"Grade_{i}_Name", out var gName) ? gName : null;
            var price = data.TryGetValue($"Grade_{i}_Price", out var gPrice) && long.TryParse(gPrice, out var p) ? p : 0L;

            grades.Add(new GradePrice(gradeId, gradeName, price, currencyCode));
        }

        return new PriceSetSnapshot(priceSetId, priceGroupIds, grades, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Build FcPriceUpdate_req to push price changes.
    /// Legacy format: PriceSetId=01, GradeCount=N, Grade_0_Id=XX, Grade_0_Price=XXXXX, ActivationDate=yyyyMMddHHmmss
    /// </summary>
    public static JplMessage BuildPriceUpdateRequest(PriceUpdateCommand command)
    {
        var data = new Dictionary<string, string>
        {
            ["PriceSetId"] = "01",
            ["GradeCount"] = command.Updates.Count.ToString(),
        };

        for (var i = 0; i < command.Updates.Count; i++)
        {
            data[$"Grade_{i}_Id"] = command.Updates[i].GradeId;
            data[$"Grade_{i}_Price"] = command.Updates[i].NewPriceMinorUnits.ToString("00000");
        }

        if (command.ActivationTime.HasValue)
        {
            data["ActivationDate"] = command.ActivationTime.Value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        return new JplMessage(Name: PriceUpdateRequest, Data: data);
    }

    /// <summary>Validate a price update response.</summary>
    public static PriceUpdateResult ValidatePriceUpdateResponse(JplMessage response)
    {
        var resultCode = response.Data?.TryGetValue("ResultCode", out var rc) == true ? rc : null;
        return resultCode == ResultOk
            ? new PriceUpdateResult(true)
            : new PriceUpdateResult(false, $"Price update failed: ResultCode={resultCode ?? "missing"}");
    }
}

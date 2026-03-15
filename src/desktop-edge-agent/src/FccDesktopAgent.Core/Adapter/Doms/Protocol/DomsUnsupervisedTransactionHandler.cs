using FccDesktopAgent.Core.Adapter.Doms.Jpl;
using FccDesktopAgent.Core.Adapter.Doms.Model;

namespace FccDesktopAgent.Core.Adapter.Doms.Protocol;

/// <summary>
/// Handles unsupervised transaction retrieval from DOMS.
/// Ported from legacy DPP port 5004/5005 FpUnsupervisedTransaction parsing.
/// </summary>
internal static class DomsUnsupervisedTransactionHandler
{
    public const string ReadRequest = "FpUnsupTrans_read_req";
    public const string ReadResponse = "FpUnsupTrans_read_resp";

    private const string ResultOk = "0";
    private const string ResultEmpty = "1";

    /// <summary>Build FpUnsupTrans_read_req. fpId=0 means all pumps.</summary>
    public static JplMessage BuildReadRequest(int fpId = 0)
        => new(Name: ReadRequest, Data: new Dictionary<string, string>
        {
            ["FpId"] = fpId.ToString(),
        });

    /// <summary>
    /// Parse unsupervised transactions from the response.
    /// Unsupervised transactions have the same fields as supervised ones.
    /// </summary>
    public static IReadOnlyList<DomsJplTransactionDto> ParseReadResponse(JplMessage response)
    {
        if (response.Name != ReadResponse) return [];

        var data = response.Data;
        if (data is null) return [];

        var resultCode = data.TryGetValue("ResultCode", out var rc) ? rc : null;
        if (resultCode is ResultEmpty or null) return [];
        if (resultCode != ResultOk) return [];

        var count = data.TryGetValue("TransCount", out var tcStr) && int.TryParse(tcStr, out var tc) ? tc : 0;
        if (count == 0) return [];

        if (count == 1)
        {
            try
            {
                return [DomsSupParamParser.Parse(data, bufferIndex: 0)];
            }
            catch
            {
                return [];
            }
        }

        var results = new List<DomsJplTransactionDto>(count);
        for (var i = 0; i < count; i++)
        {
            try
            {
                var prefix = $"Trans_{i}_";
                var indexedData = data
                    .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value);
                results.Add(DomsSupParamParser.Parse(indexedData, bufferIndex: i));
            }
            catch
            {
                // Skip malformed transactions
            }
        }

        return results;
    }
}

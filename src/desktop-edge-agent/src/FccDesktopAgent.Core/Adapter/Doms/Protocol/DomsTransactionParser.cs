using FccDesktopAgent.Core.Adapter.Doms.Jpl;
using FccDesktopAgent.Core.Adapter.Doms.Model;

namespace FccDesktopAgent.Core.Adapter.Doms.Protocol;

/// <summary>
/// Handles the DOMS supervised transaction buffer: lock -> read -> clear.
///
/// DOMS maintains a "supervised buffer" of completed transactions per fuelling point.
/// To safely retrieve transactions:
///   1. Lock the buffer (FpSupTrans_lock_req) -- prevents new transactions from entering
///   2. Read transactions (FpSupTrans_read_req) -- returns buffered transactions
///   3. Clear the buffer (FpSupTrans_clear_req) -- removes read transactions
///
/// If clear is not sent, transactions remain in the buffer and will be returned again.
/// </summary>
public static class DomsTransactionParser
{
    // -- JPL message names -------------------------------------------------------

    public const string LockRequest = "FpSupTrans_lock_req";
    public const string LockResponse = "FpSupTrans_lock_resp";
    public const string ReadRequest = "FpSupTrans_read_req";
    public const string ReadResponse = "FpSupTrans_read_resp";
    public const string ClearRequest = "FpSupTrans_clear_req";
    public const string ClearResponse = "FpSupTrans_clear_resp";

    /// <summary>Result code indicating success.</summary>
    public const string ResultOk = "0";

    /// <summary>Result code indicating no transactions available.</summary>
    public const string ResultEmpty = "1";

    // -- Lock --------------------------------------------------------------------

    /// <summary>
    /// Build a lock request for the supervised transaction buffer.
    /// </summary>
    /// <param name="fpId">Fuelling point ID (0 = all pumps).</param>
    public static JplMessage BuildLockRequest(int fpId = 0)
    {
        return new JplMessage(
            Name: LockRequest,
            Data: new Dictionary<string, string>
            {
                ["FpId"] = fpId.ToString(),
            });
    }

    /// <summary>
    /// Validate a lock response.
    /// </summary>
    /// <returns>true if lock was acquired successfully.</returns>
    public static bool ValidateLockResponse(JplMessage response)
    {
        if (response.Name != LockResponse)
            return false;

        if (response.Data is null || !response.Data.TryGetValue("ResultCode", out var resultCode))
            return false;

        return resultCode == ResultOk;
    }

    // -- Read --------------------------------------------------------------------

    /// <summary>
    /// Build a read request for the supervised transaction buffer.
    /// </summary>
    /// <param name="fpId">Fuelling point ID (0 = all pumps).</param>
    /// <param name="bufferIndex">Starting buffer index to read from.</param>
    public static JplMessage BuildReadRequest(int fpId = 0, int bufferIndex = 0)
    {
        return new JplMessage(
            Name: ReadRequest,
            Data: new Dictionary<string, string>
            {
                ["FpId"] = fpId.ToString(),
                ["BufferIndex"] = bufferIndex.ToString(),
            });
    }

    /// <summary>
    /// Parse a read response into transaction DTOs.
    /// </summary>
    /// <param name="response">JPL response message.</param>
    /// <returns>List of parsed transactions, or empty if buffer was empty.</returns>
    public static IReadOnlyList<DomsJplTransactionDto> ParseReadResponse(JplMessage response)
    {
        if (response.Name != ReadResponse)
            return [];

        if (response.Data is null)
            return [];

        var data = response.Data;

        if (!data.TryGetValue("ResultCode", out var resultCode))
            return [];

        if (resultCode == ResultEmpty)
            return [];

        if (resultCode != ResultOk)
            return [];

        if (!data.TryGetValue("TransCount", out var countStr) || !int.TryParse(countStr, out var count))
            return [];

        if (count == 0)
            return [];

        // For a single transaction, parse directly from the data map.
        // For multiple transactions, data contains indexed fields (Trans_0_*, Trans_1_*, etc.).
        if (count == 1)
        {
            return [DomsSupParamParser.Parse(data, bufferIndex: 0)];
        }

        var results = new List<DomsJplTransactionDto>(count);
        for (var index = 0; index < count; index++)
        {
            try
            {
                var indexedData = ExtractIndexedTransaction(data, index);
                results.Add(DomsSupParamParser.Parse(indexedData, bufferIndex: index));
            }
            catch
            {
                // Skip malformed individual transactions.
            }
        }

        return results;
    }

    // -- Clear -------------------------------------------------------------------

    /// <summary>
    /// Build a clear request to remove read transactions from the buffer.
    /// </summary>
    /// <param name="fpId">Fuelling point ID.</param>
    /// <param name="count">Number of transactions to clear.</param>
    public static JplMessage BuildClearRequest(int fpId = 0, int count = 0)
    {
        return new JplMessage(
            Name: ClearRequest,
            Data: new Dictionary<string, string>
            {
                ["FpId"] = fpId.ToString(),
                ["TransCount"] = count.ToString(),
            });
    }

    /// <summary>
    /// Validate a clear response.
    /// </summary>
    /// <returns>true if clear was successful.</returns>
    public static bool ValidateClearResponse(JplMessage response)
    {
        if (response.Name != ClearResponse)
            return false;

        if (response.Data is null || !response.Data.TryGetValue("ResultCode", out var resultCode))
            return false;

        return resultCode == ResultOk;
    }

    // -- Helpers -----------------------------------------------------------------

    /// <summary>
    /// Extract indexed transaction fields from a multi-transaction response.
    /// Fields are prefixed with "Trans_{index}_" (e.g., "Trans_0_TransId", "Trans_0_FpId").
    /// </summary>
    private static Dictionary<string, string> ExtractIndexedTransaction(
        IReadOnlyDictionary<string, string> data, int index)
    {
        var prefix = $"Trans_{index}_";
        var result = new Dictionary<string, string>();

        foreach (var kvp in data)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                result[kvp.Key[prefix.Length..]] = kvp.Value;
            }
        }

        return result;
    }
}

using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Adapter.Doms.Model;

namespace FccDesktopAgent.Core.Adapter.Doms.Protocol;

/// <summary>
/// Parses DOMS supervised transaction parameters from JPL message data maps.
///
/// The FpSupTrans response contains transaction data in a flat key-value map.
/// This parser extracts and validates the fields, returning a typed DTO.
/// </summary>
public static class DomsSupParamParser
{
    /// <summary>
    /// Parse a JPL data map from a FpSupTrans read response into a DomsJplTransactionDto.
    /// </summary>
    /// <param name="data">Key-value map from JplMessage.Data.</param>
    /// <param name="bufferIndex">The supervised buffer index this transaction was read from.</param>
    /// <returns>Parsed transaction DTO.</returns>
    /// <exception cref="ArgumentException">If required fields are missing or invalid.</exception>
    public static DomsJplTransactionDto Parse(IReadOnlyDictionary<string, string> data, int bufferIndex)
    {
        // G-05: Decode TransInfoMask if present in the supervised buffer response
        TransactionInfoMask? infoMask = null;
        if (data.TryGetValue("TransInfoMask", out var maskStr) && int.TryParse(maskStr, out var maskBits))
        {
            infoMask = TransactionInfoMask.FromBits(maskBits);

            if (infoMask.MoneyDueIncluded && data.TryGetValue("MoneyDue", out var mdStr) && long.TryParse(mdStr, out var md))
                infoMask = infoMask with { MoneyDue = md };
            if (data.TryGetValue("TransSeqNo", out var seqStr) && int.TryParse(seqStr, out var seq))
                infoMask = infoMask with { TransSequenceNo = seq };
            if (data.TryGetValue("TransLockId", out var lockStr) && int.TryParse(lockStr, out var lockId))
                infoMask = infoMask with { TransLockId = lockId };
        }

        return new DomsJplTransactionDto(
            TransactionId: RequireField(data, "TransId"),
            FpId: ParseIntOrFail(RequireField(data, "FpId"), "FpId"),
            NozzleId: ParseIntOrFail(RequireField(data, "NozzleId"), "NozzleId"),
            ProductCode: RequireField(data, "ProductCode"),
            VolumeCl: ParseLongOrFail(RequireField(data, "Volume"), "Volume"),
            AmountX10: ParseLongOrFail(RequireField(data, "Amount"), "Amount"),
            UnitPriceX10: ParseLongOrFail(RequireField(data, "UnitPrice"), "UnitPrice"),
            Timestamp: RequireField(data, "Timestamp"),
            AttendantId: data.TryGetValue("AttendantId", out var aid) ? aid : null,
            BufferIndex: bufferIndex,
            InfoMask: infoMask);
    }

    private static string RequireField(IReadOnlyDictionary<string, string> data, string key)
    {
        if (data.TryGetValue(key, out var value))
            return value;

        throw new ArgumentException($"Missing required DOMS field: {key}");
    }

    private static int ParseIntOrFail(string value, string fieldName)
    {
        if (int.TryParse(value, out var result))
            return result;

        throw new ArgumentException($"Invalid integer value for {fieldName}: '{value}'");
    }

    private static long ParseLongOrFail(string value, string fieldName)
    {
        if (long.TryParse(value, out var result))
            return result;

        throw new ArgumentException($"Invalid long value for {fieldName}: '{value}'");
    }
}

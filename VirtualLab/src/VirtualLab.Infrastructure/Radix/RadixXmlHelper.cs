using System.Security.Cryptography;
using System.Text;

namespace VirtualLab.Infrastructure.Radix;

/// <summary>
/// Utility class for Radix FDC XML protocol parsing and generation.
/// Handles AUTH_DATA parsing, HOST_REQ parsing, response building, and SHA-1 signatures.
/// </summary>
public static class RadixXmlHelper
{
    // -----------------------------------------------------------------------
    // AUTH_DATA parsing
    // -----------------------------------------------------------------------

    public static RadixSimAuthRequest ParseAuthRequest(string xml)
    {
        int pump = ParseIntElement(xml, "PUMP", 1);
        int fp = ParseIntElement(xml, "FP", 1);
        bool authorize = string.Equals(ExtractElementText(xml, "AUTH"), "TRUE", StringComparison.OrdinalIgnoreCase);
        int product = ParseIntElement(xml, "PROD", 0);
        string presetVolume = ExtractElementText(xml, "PRESET_VOLUME") ?? "0.00";
        string presetAmount = ExtractElementText(xml, "PRESET_AMOUNT") ?? "0.00";
        string? customerName = ExtractElementText(xml, "CUSTNAME");
        int? customerIdType = ParseNullableIntElement(xml, "CUSTIDTYPE");
        string? customerId = ExtractElementText(xml, "CUSTID");
        string? mobileNumber = ExtractElementText(xml, "MOBILENUM");
        string token = ExtractElementText(xml, "TOKEN") ?? "0";

        return new RadixSimAuthRequest(
            pump, fp, authorize, product,
            presetVolume, presetAmount,
            customerName, customerIdType, customerId, mobileNumber,
            token);
    }

    // -----------------------------------------------------------------------
    // HOST_REQ parsing
    // -----------------------------------------------------------------------

    public static RadixSimHostRequest ParseHostRequest(string xml)
    {
        int cmdCode = ParseIntAttribute(xml, "CMD_CODE", -1);
        string cmdName = ExtractAttributeValue(xml, "CMD_NAME") ?? "";
        string token = ExtractAttributeValue(xml, "TOKEN") ?? "0";
        string? mode = ExtractAttributeValue(xml, "MODE");
        string? signature = ExtractRawElementText(xml, "SIGNATURE");

        return new RadixSimHostRequest(cmdCode, cmdName, token, mode, signature);
    }

    // -----------------------------------------------------------------------
    // Auth response XML builder
    // -----------------------------------------------------------------------

    public static string BuildAuthResponse(int ackCode, string ackMsg, string sharedSecret)
    {
        string now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        string time = DateTimeOffset.UtcNow.ToString("HH:mm:ss");

        string fdcAckContent =
            $"""<FDCACK><DATE>{now}</DATE><TIME>{time}</TIME><ACKCODE>{ackCode}</ACKCODE><ACKMSG>{ackMsg}</ACKMSG></FDCACK>""";

        string signature = ComputeSha1(fdcAckContent + sharedSecret);
        return $"""<?xml version="1.0" encoding="utf-8"?><FDCMS>{fdcAckContent}<FDCSIGNATURE>{signature}</FDCSIGNATURE></FDCMS>""";
    }

    // -----------------------------------------------------------------------
    // Transaction response XML builder
    // -----------------------------------------------------------------------

    public static string BuildTransactionResponse(
        int respCode, string respMsg, long token, string sharedSecret,
        RadixSimulator.RadixSimulatedTransaction? transaction = null,
        int usnCode = 12345)
    {
        StringBuilder table = new();
        table.Append($"""<TABLE><ANS RESP_CODE="{respCode}" RESP_MSG="{respMsg}" TOKEN="{token}" />""");

        if (transaction is not null)
        {
            table.Append($"""<TRN AMO="{transaction.Amount}" EFD_ID="{transaction.EfdId}" """);
            table.Append($"""FDC_DATE="{transaction.FdcDate}" FDC_TIME="{transaction.FdcTime}" """);
            table.Append($"""FDC_NAME="RADIX_VL_SIM" FDC_NUM="{usnCode}" """);
            table.Append($"""FDC_PROD="{transaction.ProductId}" FDC_PROD_NAME="{transaction.ProductName}" """);
            table.Append($"""FDC_SAVE_NUM="{transaction.SaveNum}" FDC_TANK="1" """);
            table.Append($"""FP="{transaction.PumpNumber}" NOZ="{transaction.NozzleNumber}" """);
            table.Append($"""PRICE="{transaction.Price}" PUMP_ADDR="{transaction.PumpNumber}" """);
            table.Append($"""RDG_DATE="{transaction.FdcDate}" RDG_TIME="{transaction.FdcTime}" """);
            table.Append($"""RDG_ID="{transaction.Id}" RDG_INDEX="1" """);
            table.Append($"""RDG_PROD="{transaction.ProductId}" RDG_SAVE_NUM="{transaction.SaveNum}" """);
            table.Append($"""REG_ID="1" ROUND_TYPE="0" VOL="{transaction.Volume}" />""");
        }

        table.Append("</TABLE>");

        string tableContent = table.ToString();
        string signature = ComputeSha1(tableContent + sharedSecret);

        return $"""<?xml version="1.0" encoding="UTF-8"?><FDC_RESP>{tableContent}<SIGNATURE>{signature}</SIGNATURE></FDC_RESP>""";
    }

    // -----------------------------------------------------------------------
    // Product list XML builder
    // -----------------------------------------------------------------------

    public static string BuildProductResponse(
        long token, string sharedSecret,
        IReadOnlyDictionary<int, RadixSimulator.RadixProductEntry> products)
    {
        StringBuilder productElements = new();
        foreach (var kvp in products)
        {
            productElements.Append(
                $"""<PRODUCT ID="{kvp.Key}" NAME="{kvp.Value.Name}" PRICE="{kvp.Value.Price}" />""");
        }

        string tableContent =
            $"""<TABLE><ANS RESP_CODE="201" RESP_MSG="DATA" TOKEN="{token}" />{productElements}</TABLE>""";

        string signature = ComputeSha1(tableContent + sharedSecret);
        return $"""<?xml version="1.0" encoding="UTF-8"?><FDC_RESP>{tableContent}<SIGNATURE>{signature}</SIGNATURE></FDC_RESP>""";
    }

    // -----------------------------------------------------------------------
    // SHA-1 signature
    // -----------------------------------------------------------------------

    public static string ComputeSha1(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        byte[] hash = SHA1.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public static bool ValidateAuthSignature(string xml, string sharedSecret)
    {
        string? authDataContent = ExtractRawElement(xml, "AUTH_DATA");
        string? signature = ExtractRawElementText(xml, "FDCSIGNATURE");

        if (authDataContent is null || signature is null)
        {
            return true; // No signature to validate — allow simple test payloads
        }

        string expected = ComputeSha1(authDataContent + sharedSecret);
        return string.Equals(expected, signature, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ValidateTransactionSignature(string xml, string sharedSecret)
    {
        string? reqContent = ExtractRawElement(xml, "REQ");
        string? signature = ExtractRawElementText(xml, "SIGNATURE");

        if (reqContent is null || signature is null)
        {
            return true; // No signature to validate
        }

        string expected = ComputeSha1(reqContent + sharedSecret);
        return string.Equals(expected, signature, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // XML parsing helpers
    // -----------------------------------------------------------------------

    private static string? ExtractElementText(string xml, string tagName)
    {
        string openTag = $"<{tagName}>";
        string closeTag = $"</{tagName}>";
        int startIdx = xml.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return null;
        int endIdx = xml.IndexOf(closeTag, startIdx, StringComparison.OrdinalIgnoreCase);
        if (endIdx < 0) return null;
        string value = xml[(startIdx + openTag.Length)..endIdx].Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int ParseIntElement(string xml, string tagName, int defaultValue)
    {
        string? value = ExtractElementText(xml, tagName);
        return value is not null && int.TryParse(value, out int result) ? result : defaultValue;
    }

    private static int? ParseNullableIntElement(string xml, string tagName)
    {
        string? value = ExtractElementText(xml, tagName);
        return value is not null && int.TryParse(value, out int result) ? result : null;
    }

    internal static string ExtractAttributeValue(string xml, string attributeName)
    {
        string searchPattern = $"{attributeName}=\"";
        int startIdx = xml.IndexOf(searchPattern, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return string.Empty;

        startIdx += searchPattern.Length;
        int endIdx = xml.IndexOf('"', startIdx);
        if (endIdx < 0) return string.Empty;

        return xml[startIdx..endIdx];
    }

    private static int ParseIntAttribute(string xml, string attributeName, int defaultValue)
    {
        string value = ExtractAttributeValue(xml, attributeName);
        return int.TryParse(value, out int result) ? result : defaultValue;
    }

    private static string? ExtractRawElement(string xml, string tagName)
    {
        string openTag = $"<{tagName}";
        string closeTag = $"</{tagName}>";
        int startIdx = xml.IndexOf(openTag, StringComparison.Ordinal);
        if (startIdx < 0) return null;
        int endIdx = xml.IndexOf(closeTag, startIdx, StringComparison.Ordinal);
        if (endIdx < 0) return null;
        return xml[startIdx..(endIdx + closeTag.Length)];
    }

    internal static string? ExtractRawElementText(string xml, string tagName)
    {
        string openTag = $"<{tagName}>";
        string closeTag = $"</{tagName}>";
        int startIdx = xml.IndexOf(openTag, StringComparison.Ordinal);
        if (startIdx < 0) return null;
        int endIdx = xml.IndexOf(closeTag, startIdx, StringComparison.Ordinal);
        if (endIdx < 0) return null;
        return xml[(startIdx + openTag.Length)..endIdx].Trim();
    }
}

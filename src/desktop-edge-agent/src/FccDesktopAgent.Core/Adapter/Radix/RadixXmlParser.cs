using System.Xml.Linq;

namespace FccDesktopAgent.Core.Adapter.Radix;

/// <summary>
/// Radix FDC XML response parser.
///
/// Parses XML responses from the Radix FCC into typed DTOs:
/// <list type="bullet">
///   <item>Transaction responses (<c>&lt;FDC_RESP&gt;</c> with <c>&lt;TRN&gt;</c> data)</item>
///   <item>Auth/pre-auth acknowledgments (<c>&lt;FDCMS&gt;</c> with <c>&lt;FDCACK&gt;</c>)</item>
///   <item>Product list responses (<c>&lt;FDC_RESP&gt;</c> with <c>&lt;PRODUCT&gt;</c> elements)</item>
/// </list>
///
/// Also validates SHA-1 signatures on incoming responses using <see cref="RadixSignatureHelper"/>.
///
/// Uses <see cref="XDocument"/> (<c>System.Xml.Linq</c>) for XML parsing.
/// Missing/empty XML attributes default to empty strings — no exceptions thrown
/// for absent optional data.
/// </summary>
public static class RadixXmlParser
{
    // -----------------------------------------------------------------------
    // Public — Response parsing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses a transaction response (<c>&lt;FDC_RESP&gt;</c>) XML.
    ///
    /// Extracts <c>&lt;ANS&gt;</c> attributes (RESP_CODE, RESP_MSG, TOKEN) and optional
    /// <c>&lt;TRN&gt;</c>, <c>&lt;RFID_CARD&gt;</c>, <c>&lt;DISCOUNT&gt;</c>, and <c>&lt;CUST_DATA&gt;</c> child elements.
    ///
    /// Response codes:
    /// <list type="bullet">
    ///   <item>201: Success with transaction data</item>
    ///   <item>205: No transaction available (buffer empty)</item>
    ///   <item>30: Unsolicited push transaction</item>
    ///   <item>206, 251, 253, 255: Error codes</item>
    /// </list>
    /// </summary>
    /// <param name="xml">Raw XML string from the FDC.</param>
    /// <returns>Parsed <see cref="RadixTransactionResponse"/> on success, or null for malformed/invalid XML.</returns>
    public static RadixTransactionResponse? ParseTransactionResponse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);

            var ansElement = doc.Descendants("ANS").FirstOrDefault();
            if (ansElement is null)
                return null;

            if (!int.TryParse(Attr(ansElement, "RESP_CODE"), out var respCode))
                return null;

            var respMsg = Attr(ansElement, "RESP_MSG");
            var token = Attr(ansElement, "TOKEN");

            var signature = doc.Descendants("SIGNATURE").FirstOrDefault()?.Value.Trim() ?? "";

            // Parse child elements only when transaction data is expected
            var transaction = (respCode is 201 or 30)
                ? ParseTrnElement(doc)
                : null;

            var rfidCard = ParseRfidCardElement(doc);
            var discount = ParseDiscountElement(doc);
            var customerData = ParseCustDataElement(doc);

            return new RadixTransactionResponse(
                RespCode: respCode,
                RespMsg: respMsg,
                Token: token,
                Transaction: transaction,
                RfidCard: rfidCard,
                Discount: discount,
                CustomerData: customerData,
                Signature: signature);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses an auth/pre-auth acknowledgment response (<c>&lt;FDCMS&gt;</c>) XML.
    ///
    /// Extracts <c>&lt;FDCACK&gt;</c> child elements: DATE, TIME, ACKCODE, ACKMSG,
    /// and the <c>&lt;FDCSIGNATURE&gt;</c> value.
    ///
    /// Acknowledgment codes:
    /// <list type="bullet">
    ///   <item>0: Success</item>
    ///   <item>251: Signature error</item>
    ///   <item>255: Bad XML format</item>
    ///   <item>256: Bad header format</item>
    ///   <item>258: Pump not ready</item>
    ///   <item>260: DSB offline</item>
    /// </list>
    /// </summary>
    /// <param name="xml">Raw XML string from the FDC.</param>
    /// <returns>Parsed <see cref="RadixAuthResponse"/> on success, or null for malformed/invalid XML.</returns>
    public static RadixAuthResponse? ParseAuthResponse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);

            var fdcAck = doc.Descendants("FDCACK").FirstOrDefault();
            if (fdcAck is null)
                return null;

            if (!int.TryParse(ChildText(fdcAck, "ACKCODE"), out var ackCode))
                return null;

            var signature = doc.Descendants("FDCSIGNATURE").FirstOrDefault()?.Value.Trim() ?? "";

            return new RadixAuthResponse(
                Date: ChildText(fdcAck, "DATE"),
                Time: ChildText(fdcAck, "TIME"),
                AckCode: ackCode,
                AckMsg: ChildText(fdcAck, "ACKMSG"),
                Signature: signature);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a product list response (<c>&lt;FDC_RESP&gt;</c> from CMD_CODE=55).
    ///
    /// Extracts <c>&lt;ANS&gt;</c> attributes and all <c>&lt;PRODUCT&gt;</c> child elements
    /// with their ID, NAME, and PRICE attributes.
    /// </summary>
    /// <param name="xml">Raw XML string from the FDC.</param>
    /// <returns>Parsed <see cref="RadixProductResponse"/> on success, or null for malformed/invalid XML.</returns>
    public static RadixProductResponse? ParseProductResponse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);

            var ansElement = doc.Descendants("ANS").FirstOrDefault();
            if (ansElement is null)
                return null;

            if (!int.TryParse(Attr(ansElement, "RESP_CODE"), out var respCode))
                return null;

            var respMsg = Attr(ansElement, "RESP_MSG");

            var products = new List<RadixProductData>();
            foreach (var elem in doc.Descendants("PRODUCT"))
            {
                if (!int.TryParse(Attr(elem, "ID"), out var id))
                    continue;

                products.Add(new RadixProductData(
                    Id: id,
                    Name: Attr(elem, "NAME"),
                    Price: Attr(elem, "PRICE")));
            }

            return new RadixProductResponse(
                RespCode: respCode,
                RespMsg: respMsg,
                Products: products);
        }
        catch
        {
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Public — Signature validation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Validates the SHA-1 signature of a transaction response (<c>&lt;FDC_RESP&gt;</c>).
    ///
    /// Extracts the raw <c>&lt;TABLE&gt;...&lt;/TABLE&gt;</c> content from the XML string
    /// (preserving exact whitespace for character-accurate hash) and validates
    /// it against the <c>&lt;SIGNATURE&gt;</c> value using <see cref="RadixSignatureHelper"/>.
    /// </summary>
    /// <param name="xml">Raw XML string from the FDC.</param>
    /// <param name="sharedSecret">Shared secret password configured for this FCC.</param>
    /// <returns><c>true</c> if the signature is valid, <c>false</c> if invalid or extraction fails.</returns>
    public static bool ValidateTransactionResponseSignature(string xml, string sharedSecret)
    {
        var tableContent = ExtractRawElement(xml, "TABLE");
        if (tableContent is null) return false;

        var signature = ExtractRawElementText(xml, "SIGNATURE");
        if (signature is null) return false;

        return RadixSignatureHelper.ValidateSignature(tableContent, signature, sharedSecret);
    }

    /// <summary>
    /// Validates the SHA-1 signature of an auth response (<c>&lt;FDCMS&gt;</c>).
    ///
    /// Extracts the raw <c>&lt;FDCACK&gt;...&lt;/FDCACK&gt;</c> content from the XML string
    /// and validates it against the <c>&lt;FDCSIGNATURE&gt;</c> value.
    /// </summary>
    /// <param name="xml">Raw XML string from the FDC.</param>
    /// <param name="sharedSecret">Shared secret password configured for this FCC.</param>
    /// <returns><c>true</c> if the signature is valid, <c>false</c> if invalid or extraction fails.</returns>
    public static bool ValidateAuthResponseSignature(string xml, string sharedSecret)
    {
        var fdcAckContent = ExtractRawElement(xml, "FDCACK");
        if (fdcAckContent is null) return false;

        var signature = ExtractRawElementText(xml, "FDCSIGNATURE");
        if (signature is null) return false;

        return RadixSignatureHelper.ValidateSignature(fdcAckContent, signature, sharedSecret);
    }

    // -----------------------------------------------------------------------
    // Private — XDocument helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Safe attribute getter — returns empty string for absent attributes.
    /// </summary>
    private static string Attr(XElement element, string name)
    {
        return element.Attribute(name)?.Value ?? "";
    }

    /// <summary>
    /// Returns the text content of the first child element with the given name, or empty string.
    /// </summary>
    private static string ChildText(XElement parent, string tagName)
    {
        return parent.Element(tagName)?.Value ?? "";
    }

    // -----------------------------------------------------------------------
    // Private — Element parsing
    // -----------------------------------------------------------------------

    private static RadixTransactionData? ParseTrnElement(XDocument doc)
    {
        var trn = doc.Descendants("TRN").FirstOrDefault();
        if (trn is null || !trn.HasAttributes)
            return null;

        return new RadixTransactionData(
            Amo: Attr(trn, "AMO"),
            EfdId: Attr(trn, "EFD_ID"),
            FdcDate: Attr(trn, "FDC_DATE"),
            FdcTime: Attr(trn, "FDC_TIME"),
            FdcName: Attr(trn, "FDC_NAME"),
            FdcNum: Attr(trn, "FDC_NUM"),
            FdcProd: Attr(trn, "FDC_PROD"),
            FdcProdName: Attr(trn, "FDC_PROD_NAME"),
            FdcSaveNum: Attr(trn, "FDC_SAVE_NUM"),
            FdcTank: Attr(trn, "FDC_TANK"),
            Fp: Attr(trn, "FP"),
            Noz: Attr(trn, "NOZ"),
            Price: Attr(trn, "PRICE"),
            PumpAddr: Attr(trn, "PUMP_ADDR"),
            RdgDate: Attr(trn, "RDG_DATE"),
            RdgTime: Attr(trn, "RDG_TIME"),
            RdgId: Attr(trn, "RDG_ID"),
            RdgIndex: Attr(trn, "RDG_INDEX"),
            RdgProd: Attr(trn, "RDG_PROD"),
            RdgSaveNum: Attr(trn, "RDG_SAVE_NUM"),
            RegId: Attr(trn, "REG_ID"),
            RoundType: Attr(trn, "ROUND_TYPE"),
            Vol: Attr(trn, "VOL"));
    }

    private static RadixRfidCardData? ParseRfidCardElement(XDocument doc)
    {
        var elem = doc.Descendants("RFID_CARD").FirstOrDefault();
        if (elem is null || !elem.HasAttributes)
            return null;

        return new RadixRfidCardData(
            CardType: Attr(elem, "CARD_TYPE"),
            CustContact: Attr(elem, "CUST_CONTACT"),
            CustId: Attr(elem, "CUST_ID"),
            CustIdType: Attr(elem, "CUST_IDTYPE"),
            CustName: Attr(elem, "CUST_NAME"),
            Discount: Attr(elem, "DISCOUNT"),
            DiscountType: Attr(elem, "DISCOUNT_TYPE"),
            Num: Attr(elem, "NUM"),
            Num10: Attr(elem, "NUM_10"),
            PayMethod: Attr(elem, "PAY_METHOD"),
            ProductEnabled: Attr(elem, "PRODUCT_ENABLED"),
            Used: Attr(elem, "USED"));
    }

    private static RadixDiscountData? ParseDiscountElement(XDocument doc)
    {
        var elem = doc.Descendants("DISCOUNT").FirstOrDefault();
        if (elem is null || !elem.HasAttributes)
            return null;

        return new RadixDiscountData(
            AmoDiscount: Attr(elem, "AMO_DISCOUNT"),
            AmoNew: Attr(elem, "AMO_NEW"),
            AmoOrigin: Attr(elem, "AMO_ORIGIN"),
            DiscountType: Attr(elem, "DISCOUNT_TYPE"),
            PriceDiscount: Attr(elem, "PRICE_DISCOUNT"),
            PriceNew: Attr(elem, "PRICE_NEW"),
            PriceOrigin: Attr(elem, "PRICE_ORIGIN"),
            VolOrigin: Attr(elem, "VOL_ORIGIN"));
    }

    private static RadixCustomerData? ParseCustDataElement(XDocument doc)
    {
        var elem = doc.Descendants("CUST_DATA").FirstOrDefault();
        if (elem is null || !elem.HasAttributes)
            return null;

        if (!int.TryParse(elem.Attribute("USED")?.Value?.Trim(), out var used))
            return null;

        return new RadixCustomerData(Used: used);
    }

    // -----------------------------------------------------------------------
    // Private — Raw XML extraction for signature validation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts raw element text including its tags from the XML string.
    ///
    /// Returns the substring from <c>&lt;tagName</c> through <c>&lt;/tagName&gt;</c> inclusive,
    /// preserving exact whitespace for signature validation.
    /// </summary>
    private static string? ExtractRawElement(string xml, string tagName)
    {
        var openTag = $"<{tagName}";
        var closeTag = $"</{tagName}>";
        var startIdx = xml.IndexOf(openTag, StringComparison.Ordinal);
        if (startIdx < 0) return null;

        var endIdx = xml.IndexOf(closeTag, startIdx, StringComparison.Ordinal);
        if (endIdx < 0) return null;

        return xml.Substring(startIdx, endIdx - startIdx + closeTag.Length);
    }

    /// <summary>
    /// Extracts the text content between <c>&lt;tagName&gt;</c> and <c>&lt;/tagName&gt;</c>,
    /// trimming whitespace.
    /// </summary>
    private static string? ExtractRawElementText(string xml, string tagName)
    {
        var openTag = $"<{tagName}>";
        var closeTag = $"</{tagName}>";
        var startIdx = xml.IndexOf(openTag, StringComparison.Ordinal);
        if (startIdx < 0) return null;

        var endIdx = xml.IndexOf(closeTag, startIdx, StringComparison.Ordinal);
        if (endIdx < 0) return null;

        return xml.Substring(startIdx + openTag.Length, endIdx - startIdx - openTag.Length).Trim();
    }
}

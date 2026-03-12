using System.Text.Json.Serialization;

namespace FccDesktopAgent.Core.Adapter.Radix;

// ---------------------------------------------------------------------------
// Radix FCC protocol DTOs — XML request/response data classes.
//
// Radix uses HTTP POST with XML bodies on dual ports:
//   Port P   : External authorization (pre-auth)
//   Port P+1 : Transaction management, products, mode changes
//
// All decimal values (amo, vol, price) remain as Strings in DTOs.
// Conversion to Long (microlitres, minor units) happens during normalization.
// Date/time fields remain as Strings — parsing happens during normalization.
// ---------------------------------------------------------------------------

// ===== Transaction data (parsed from <TRN> element) =====

/// <summary>
/// All attributes from a Radix <c>&lt;TRN&gt;</c> XML element.
///
/// Field names follow the Radix spec mapped to C# PascalCase.
/// Decimal string fields (Amo, Vol, Price) are kept as string to avoid
/// premature floating-point conversion — normalization converts them
/// to long microlitres / minor units via decimal arithmetic.
/// </summary>
public sealed record RadixTransactionData(
    /// <summary>Amount in local currency (decimal string, e.g. "30000.0").</summary>
    [property: JsonPropertyName("amo")] string Amo,
    /// <summary>Electronic Fiscal Device receipt ID (e.g. "182AC9368989").</summary>
    [property: JsonPropertyName("efdId")] string EfdId,
    /// <summary>FDC local date (e.g. "2021-03-03").</summary>
    [property: JsonPropertyName("fdcDate")] string FdcDate,
    /// <summary>FDC local time (e.g. "21:17:53").</summary>
    [property: JsonPropertyName("fdcTime")] string FdcTime,
    /// <summary>FDC station name (e.g. "10TZ100449").</summary>
    [property: JsonPropertyName("fdcName")] string FdcName,
    /// <summary>FDC serial number — part of dedup key (e.g. "100253410").</summary>
    [property: JsonPropertyName("fdcNum")] string FdcNum,
    /// <summary>Product number (FCC internal index, e.g. "0").</summary>
    [property: JsonPropertyName("fdcProd")] string FdcProd,
    /// <summary>Product display name (e.g. "UNLEADED").</summary>
    [property: JsonPropertyName("fdcProdName")] string FdcProdName,
    /// <summary>Transaction save number — part of dedup key (e.g. "368989").</summary>
    [property: JsonPropertyName("fdcSaveNum")] string FdcSaveNum,
    /// <summary>Tank reference (may be empty).</summary>
    [property: JsonPropertyName("fdcTank")] string FdcTank,
    /// <summary>Filling point within the DSB/RDG (e.g. "0").</summary>
    [property: JsonPropertyName("fp")] string Fp,
    /// <summary>Nozzle number within the filling point (e.g. "0").</summary>
    [property: JsonPropertyName("noz")] string Noz,
    /// <summary>Unit price (decimal string, e.g. "1930").</summary>
    [property: JsonPropertyName("price")] string Price,
    /// <summary>DSB/RDG unit address (e.g. "0").</summary>
    [property: JsonPropertyName("pumpAddr")] string PumpAddr,
    /// <summary>Register date (e.g. "2021-03-03").</summary>
    [property: JsonPropertyName("rdgDate")] string RdgDate,
    /// <summary>Register time (e.g. "21:17:53").</summary>
    [property: JsonPropertyName("rdgTime")] string RdgTime,
    /// <summary>Register ID (e.g. "0").</summary>
    [property: JsonPropertyName("rdgId")] string RdgId,
    /// <summary>Register index (e.g. "0").</summary>
    [property: JsonPropertyName("rdgIndex")] string RdgIndex,
    /// <summary>Register product (e.g. "0").</summary>
    [property: JsonPropertyName("rdgProd")] string RdgProd,
    /// <summary>RDG-level save number (e.g. "1066").</summary>
    [property: JsonPropertyName("rdgSaveNum")] string RdgSaveNum,
    /// <summary>Site tax registration ID (e.g. "TZ0100551361").</summary>
    [property: JsonPropertyName("regId")] string RegId,
    /// <summary>Rounding type (e.g. "0").</summary>
    [property: JsonPropertyName("roundType")] string RoundType,
    /// <summary>Volume in litres (decimal string, e.g. "15.54").</summary>
    [property: JsonPropertyName("vol")] string Vol);

// ===== RFID Card data (parsed from <RFID_CARD> element) =====

/// <summary>
/// Attributes from a Radix <c>&lt;RFID_CARD&gt;</c> XML element.
///
/// Present in transaction responses when an RFID card was used.
/// See Radix spec section 2.11.
/// </summary>
public sealed record RadixRfidCardData(
    /// <summary>Card type identifier.</summary>
    [property: JsonPropertyName("cardType")] string CardType,
    /// <summary>Customer contact info.</summary>
    [property: JsonPropertyName("custContact")] string CustContact,
    /// <summary>Customer ID value.</summary>
    [property: JsonPropertyName("custId")] string CustId,
    /// <summary>Customer ID type (1=TIN, 2=DL, 3=Voter, 4=Passport, 5=NID, 6=NIL).</summary>
    [property: JsonPropertyName("custIdType")] string CustIdType,
    /// <summary>Customer name.</summary>
    [property: JsonPropertyName("custName")] string CustName,
    /// <summary>Discount applied via card.</summary>
    [property: JsonPropertyName("discount")] string Discount,
    /// <summary>Discount type (e.g. "PERCENT", "VALUE").</summary>
    [property: JsonPropertyName("discountType")] string DiscountType,
    /// <summary>Card number.</summary>
    [property: JsonPropertyName("num")] string Num,
    /// <summary>Card number base-10 representation.</summary>
    [property: JsonPropertyName("num10")] string Num10,
    /// <summary>Payment method.</summary>
    [property: JsonPropertyName("payMethod")] string PayMethod,
    /// <summary>Product enabled flag.</summary>
    [property: JsonPropertyName("productEnabled")] string ProductEnabled,
    /// <summary>Whether the RFID card was used (0=no, 1=yes).</summary>
    [property: JsonPropertyName("used")] string Used);

// ===== Discount data (parsed from <DISCOUNT> element) =====

/// <summary>
/// Attributes from a Radix <c>&lt;DISCOUNT&gt;</c> XML element.
///
/// Present in transaction responses when a discount was applied.
/// See Radix spec section 2.11. All monetary/volume fields are decimal strings.
/// </summary>
public sealed record RadixDiscountData(
    /// <summary>Discount amount (decimal string).</summary>
    [property: JsonPropertyName("amoDiscount")] string AmoDiscount,
    /// <summary>New amount after discount (decimal string).</summary>
    [property: JsonPropertyName("amoNew")] string AmoNew,
    /// <summary>Original amount before discount (decimal string).</summary>
    [property: JsonPropertyName("amoOrigin")] string AmoOrigin,
    /// <summary>Discount type (e.g. "PERCENT", "VALUE").</summary>
    [property: JsonPropertyName("discountType")] string DiscountType,
    /// <summary>Price discount (decimal string).</summary>
    [property: JsonPropertyName("priceDiscount")] string PriceDiscount,
    /// <summary>New price after discount (decimal string).</summary>
    [property: JsonPropertyName("priceNew")] string PriceNew,
    /// <summary>Original price before discount (decimal string).</summary>
    [property: JsonPropertyName("priceOrigin")] string PriceOrigin,
    /// <summary>Original volume (decimal string).</summary>
    [property: JsonPropertyName("volOrigin")] string VolOrigin);

// ===== Customer data (parsed from <CUST_DATA> element) =====

/// <summary>
/// Attributes from a Radix <c>&lt;CUST_DATA&gt;</c> XML element.
///
/// When USED=1, pre-auth customer data may be echoed back.
/// </summary>
public sealed record RadixCustomerData(
    /// <summary>Whether customer data was used (0=no, 1=yes).</summary>
    [property: JsonPropertyName("used")] int Used);

// ===== Response envelopes =====

/// <summary>
/// Parsed <c>&lt;FDC_RESP&gt;</c> envelope — response to transaction fetch (CMD_CODE=10)
/// or unsolicited push (RESP_CODE=30).
///
/// Contains the ANS element (respCode, respMsg, token) plus optional
/// TRN, RFID_CARD, DISCOUNT, and CUST_DATA child elements.
/// </summary>
public sealed record RadixTransactionResponse(
    /// <summary>Response code from <c>&lt;ANS RESP_CODE="..."&gt;</c>. 201=success, 205=no transaction, 30=unsolicited.</summary>
    [property: JsonPropertyName("respCode")] int RespCode,
    /// <summary>Response message from <c>&lt;ANS RESP_MSG="..."&gt;</c>.</summary>
    [property: JsonPropertyName("respMsg")] string RespMsg,
    /// <summary>Echoed token from <c>&lt;ANS TOKEN="..."&gt;</c>.</summary>
    [property: JsonPropertyName("token")] string Token,
    /// <summary>Parsed <c>&lt;TRN&gt;</c> element. Null when RESP_CODE=205 (no transaction).</summary>
    [property: JsonPropertyName("transaction")] RadixTransactionData? Transaction = null,
    /// <summary>Parsed <c>&lt;RFID_CARD&gt;</c> element. Null when not present or empty.</summary>
    [property: JsonPropertyName("rfidCard")] RadixRfidCardData? RfidCard = null,
    /// <summary>Parsed <c>&lt;DISCOUNT&gt;</c> element. Null when not present or empty.</summary>
    [property: JsonPropertyName("discount")] RadixDiscountData? Discount = null,
    /// <summary>Parsed <c>&lt;CUST_DATA&gt;</c> element. Null when not present.</summary>
    [property: JsonPropertyName("customerData")] RadixCustomerData? CustomerData = null,
    /// <summary>SHA-1 signature from <c>&lt;SIGNATURE&gt;</c> element for verification.</summary>
    [property: JsonPropertyName("signature")] string Signature = "");

/// <summary>
/// Parsed <c>&lt;FDCMS&gt;&lt;FDCACK&gt;</c> envelope — response to pre-auth (AUTH_DATA)
/// commands on the authorization port P.
/// </summary>
public sealed record RadixAuthResponse(
    /// <summary>FDC acknowledgment date (e.g. "2021-03-01").</summary>
    [property: JsonPropertyName("date")] string Date,
    /// <summary>FDC acknowledgment time (e.g. "09:38:42").</summary>
    [property: JsonPropertyName("time")] string Time,
    /// <summary>Acknowledgment code. 0=SUCCESS, 251=SIGNATURE_ERR, 255=BAD_XML, etc.</summary>
    [property: JsonPropertyName("ackCode")] int AckCode,
    /// <summary>Acknowledgment message (e.g. "Success").</summary>
    [property: JsonPropertyName("ackMsg")] string AckMsg,
    /// <summary>SHA-1 signature from <c>&lt;FDCSIGNATURE&gt;</c> element for verification.</summary>
    [property: JsonPropertyName("signature")] string Signature);

// ===== Product data (CMD_CODE=55 response) =====

/// <summary>
/// Single product item parsed from a CMD_CODE=55 (read products) response.
/// </summary>
public sealed record RadixProductData(
    /// <summary>Product ID (FCC internal index).</summary>
    [property: JsonPropertyName("id")] int Id,
    /// <summary>Product display name (e.g. "UNLEADED").</summary>
    [property: JsonPropertyName("name")] string Name,
    /// <summary>Unit price (decimal string).</summary>
    [property: JsonPropertyName("price")] string Price);

/// <summary>
/// Parsed response to CMD_CODE=55 (read products/prices).
///
/// Also used as the heartbeat/liveness probe — a successful response
/// (respCode=201) confirms FDC is responsive.
/// </summary>
public sealed record RadixProductResponse(
    /// <summary>Response code. 201=success.</summary>
    [property: JsonPropertyName("respCode")] int RespCode,
    /// <summary>Response message.</summary>
    [property: JsonPropertyName("respMsg")] string RespMsg,
    /// <summary>List of products returned by the FDC.</summary>
    [property: JsonPropertyName("products")] IReadOnlyList<RadixProductData> Products);

// ===== Request parameters (builder input for XML construction) =====

/// <summary>
/// Parameters for building a pre-auth <c>&lt;AUTH_DATA&gt;</c> XML request.
///
/// Sent to the authorization port P. All fields map directly to
/// AUTH_DATA child elements per Radix spec section 2.6.
/// </summary>
public sealed record RadixPreAuthParams(
    /// <summary>DSB/RDG unit number (from pump address mapping).</summary>
    [property: JsonPropertyName("pump")] int Pump,
    /// <summary>Filling point within DSB/RDG (from pump address mapping).</summary>
    [property: JsonPropertyName("fp")] int Fp,
    /// <summary>true=authorize, false=cancel. Maps to <c>&lt;AUTH&gt;TRUE&lt;/AUTH&gt;</c> / <c>&lt;AUTH&gt;FALSE&lt;/AUTH&gt;</c>.</summary>
    [property: JsonPropertyName("authorize")] bool Authorize,
    /// <summary>Product number (FCC internal index, 0=all products).</summary>
    [property: JsonPropertyName("product")] int Product,
    /// <summary>Volume preset in litres (decimal string, "0.00" = not used).</summary>
    [property: JsonPropertyName("presetVolume")] string PresetVolume,
    /// <summary>Amount preset in local currency (decimal string).</summary>
    [property: JsonPropertyName("presetAmount")] string PresetAmount,
    /// <summary>Optional: customer/company name for fiscal data.</summary>
    [property: JsonPropertyName("customerName")] string? CustomerName = null,
    /// <summary>Optional: customer ID type (1=TIN, 2=DL, 3=Voter, 4=Passport, 5=NID, 6=NIL).</summary>
    [property: JsonPropertyName("customerIdType")] int? CustomerIdType = null,
    /// <summary>Optional: customer ID value.</summary>
    [property: JsonPropertyName("customerId")] string? CustomerId = null,
    /// <summary>Optional: customer phone number.</summary>
    [property: JsonPropertyName("mobileNumber")] string? MobileNumber = null,
    /// <summary>Optional: discount value.</summary>
    [property: JsonPropertyName("discountValue")] int? DiscountValue = null,
    /// <summary>Optional: discount type ("PERCENT" or "VALUE").</summary>
    [property: JsonPropertyName("discountType")] string? DiscountType = null,
    /// <summary>Correlation token (0-65535), echoed in the resulting dispense transaction.</summary>
    [property: JsonPropertyName("token")] string Token = "0");

/// <summary>
/// Parameters for building a mode change CMD_CODE=20 XML request.
///
/// Sent to the transaction port P+1.
/// </summary>
public sealed record RadixModeChangeParams(
    /// <summary>Transaction mode: 0=OFF, 1=ON_DEMAND (pull), 2=UNSOLICITED (push).</summary>
    [property: JsonPropertyName("mode")] int Mode,
    /// <summary>Request token for correlation.</summary>
    [property: JsonPropertyName("token")] string Token);

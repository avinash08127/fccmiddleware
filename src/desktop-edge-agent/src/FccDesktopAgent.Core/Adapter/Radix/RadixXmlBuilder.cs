using System.Text;

namespace FccDesktopAgent.Core.Adapter.Radix;

/// <summary>
/// Radix XML request body builder.
///
/// Builds XML request bodies for Radix FCC operations:
/// <list type="bullet">
///   <item>HOST_REQ envelope with SHA-1 signature (transaction management, port P+1)</item>
///   <item>FDCMS envelope with SHA-1 signature (external authorization, port P)</item>
/// </list>
///
/// Builder methods:
/// <list type="bullet">
///   <item><see cref="BuildTransactionRequest"/> — CMD_CODE=10, TRN_REQ</item>
///   <item><see cref="BuildTransactionAck"/> — CMD_CODE=201, SUCCESS</item>
///   <item><see cref="BuildModeChangeRequest"/> — CMD_CODE=20, MODE_CHANGE with MODE element</item>
///   <item><see cref="BuildProductReadRequest"/> — CMD_CODE=55, PRODUCT_REQ</item>
///   <item><see cref="BuildPreAuthRequest"/> — AUTH_DATA with all <see cref="RadixPreAuthParams"/> fields</item>
///   <item><see cref="BuildPreAuthCancelRequest"/> — AUTH_DATA with AUTH=FALSE</item>
///   <item><see cref="BuildHttpHeaders"/> — Custom Radix HTTP headers per Appendix B</item>
/// </list>
///
/// <b>Critical signing order:</b> Build inner content (REQ or AUTH_DATA) FIRST,
/// compute SHA-1 via <see cref="RadixSignatureHelper"/>, THEN wrap in outer envelope.
/// Whitespace in the signed content is locked before signing — do NOT
/// reformat after computing the signature.
///
/// XML is built with <see cref="StringBuilder"/> for character-exact control over output.
/// </summary>
public static class RadixXmlBuilder
{
    // -----------------------------------------------------------------------
    // Operation header constants (Appendix B)
    // -----------------------------------------------------------------------

    /// <summary>Operation header for transaction management (CMD_CODE 10, 20, 201).</summary>
    public const string OperationTransaction = "1";

    /// <summary>Operation header for products/prices read (CMD_CODE 55).</summary>
    public const string OperationProducts = "2";

    /// <summary>Operation header for day close (CMD_CODE 77).</summary>
    public const string OperationDayClose = "3";

    /// <summary>Operation header for ATG data (CMD_CODE 30/35).</summary>
    public const string OperationAtg = "4";

    /// <summary>Operation header for CSR data (CMD_CODE 40).</summary>
    public const string OperationCsr = "5";

    /// <summary>Operation header for external authorization / pre-auth.</summary>
    public const string OperationAuthorize = "Authorize";

    // -----------------------------------------------------------------------
    // Transaction Management (Port P+1) — HOST_REQ envelope
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a transaction request (CMD_CODE=10, CMD_NAME=TRN_REQ).
    ///
    /// Requests the oldest unacknowledged transaction from the FCC's FIFO buffer.
    /// The FCC responds with RESP_CODE=201 (transaction available) or
    /// RESP_CODE=205 (buffer empty).
    /// </summary>
    /// <param name="usnCode">Unique Station Number configured for this FCC.</param>
    /// <param name="token">Request token for correlation.</param>
    /// <param name="sharedSecret">Shared secret password for SHA-1 signing.</param>
    /// <returns>Complete HOST_REQ XML with signature.</returns>
    public static string BuildTransactionRequest(int usnCode, int token, string sharedSecret)
    {
        return BuildHostReq(cmdCode: 10, cmdName: "TRN_REQ", token: token.ToString(), secret: sharedSecret);
    }

    /// <summary>
    /// Builds a transaction acknowledgment (CMD_CODE=201, CMD_NAME=SUCCESS).
    ///
    /// Sent after receiving a transaction to dequeue it from the FCC's FIFO buffer.
    /// Must be sent before requesting the next transaction.
    /// </summary>
    /// <param name="usnCode">Unique Station Number configured for this FCC.</param>
    /// <param name="token">Request token (should match the fetched transaction's token).</param>
    /// <param name="sharedSecret">Shared secret password for SHA-1 signing.</param>
    /// <returns>Complete HOST_REQ XML with signature.</returns>
    public static string BuildTransactionAck(int usnCode, int token, string sharedSecret)
    {
        return BuildHostReq(cmdCode: 201, cmdName: "SUCCESS", token: token.ToString(), secret: sharedSecret);
    }

    /// <summary>
    /// Builds a mode change request (CMD_CODE=20, CMD_NAME=MODE_CHANGE).
    ///
    /// Sets the transaction transfer mode on the FCC:
    /// <list type="bullet">
    ///   <item>0 = OFF (transaction transfer disabled)</item>
    ///   <item>1 = ON_DEMAND (pull mode — host requests transactions)</item>
    ///   <item>2 = UNSOLICITED (push mode — FCC posts transactions automatically)</item>
    /// </list>
    ///
    /// Must be issued on adapter startup and after any FCC restart.
    /// </summary>
    /// <param name="usnCode">Unique Station Number configured for this FCC.</param>
    /// <param name="mode">Transaction mode (0=OFF, 1=ON_DEMAND, 2=UNSOLICITED).</param>
    /// <param name="token">Request token for correlation.</param>
    /// <param name="sharedSecret">Shared secret password for SHA-1 signing.</param>
    /// <returns>Complete HOST_REQ XML with signature.</returns>
    public static string BuildModeChangeRequest(int usnCode, int mode, int token, string sharedSecret)
    {
        return BuildHostReq(
            cmdCode: 20,
            cmdName: "MODE_CHANGE",
            token: token.ToString(),
            secret: sharedSecret,
            extraElements: [("MODE", mode.ToString())]);
    }

    /// <summary>
    /// Builds a product read request (CMD_CODE=55, CMD_NAME=PRODUCT_REQ).
    ///
    /// Reads products and prices from the FCC. Also used as the heartbeat /
    /// liveness probe since Radix has no dedicated heartbeat endpoint.
    /// </summary>
    /// <param name="usnCode">Unique Station Number configured for this FCC.</param>
    /// <param name="token">Request token for correlation.</param>
    /// <param name="sharedSecret">Shared secret password for SHA-1 signing.</param>
    /// <returns>Complete HOST_REQ XML with signature.</returns>
    public static string BuildProductReadRequest(int usnCode, int token, string sharedSecret)
    {
        return BuildHostReq(cmdCode: 55, cmdName: "PRODUCT_REQ", token: token.ToString(), secret: sharedSecret);
    }

    // -----------------------------------------------------------------------
    // External Authorization (Port P) — FDCMS envelope
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a pre-auth request with all fields from <paramref name="preAuthParams"/>.
    ///
    /// Produces an <c>&lt;FDCMS&gt;&lt;AUTH_DATA&gt;...&lt;/AUTH_DATA&gt;&lt;FDCSIGNATURE&gt;</c> XML body
    /// for the authorization port (P). Optional customer fields (CUSTNAME,
    /// CUSTIDTYPE, CUSTID, MOBILENUM, DISC_VALUE, DISC_TYPE) are included
    /// only when non-null in <paramref name="preAuthParams"/>.
    /// </summary>
    /// <param name="preAuthParams">Pre-auth parameters including pump, FP, product, presets, and optional customer data.</param>
    /// <param name="sharedSecret">Shared secret password for SHA-1 signing.</param>
    /// <returns>Complete FDCMS XML with signature.</returns>
    public static string BuildPreAuthRequest(RadixPreAuthParams preAuthParams, string sharedSecret)
    {
        var authData = BuildAuthDataContent(preAuthParams);
        var signature = RadixSignatureHelper.ComputeAuthSignature(authData, sharedSecret);
        return BuildFdcmsEnvelope(authData, signature);
    }

    /// <summary>
    /// Builds a pre-auth cancellation request.
    ///
    /// Same FDCMS/AUTH_DATA structure as <see cref="BuildPreAuthRequest"/> but with
    /// <c>&lt;AUTH&gt;FALSE&lt;/AUTH&gt;</c> to cancel an active authorization on the
    /// specified pump/FP.
    /// </summary>
    /// <param name="pumpAddr">DSB/RDG unit number.</param>
    /// <param name="fp">Filling point within the DSB/RDG.</param>
    /// <param name="usnCode">Unique Station Number configured for this FCC.</param>
    /// <param name="token">Correlation token (0-65535).</param>
    /// <param name="sharedSecret">Shared secret password for SHA-1 signing.</param>
    /// <returns>Complete FDCMS XML with signature.</returns>
    public static string BuildPreAuthCancelRequest(int pumpAddr, int fp, int usnCode, int token, string sharedSecret)
    {
        var cancelParams = new RadixPreAuthParams(
            Pump: pumpAddr,
            Fp: fp,
            Authorize: false,
            Product: 0,
            PresetVolume: "0.00",
            PresetAmount: "0",
            Token: token.ToString());

        return BuildPreAuthRequest(cancelParams, sharedSecret);
    }

    // -----------------------------------------------------------------------
    // HTTP Headers (Appendix B)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the required Radix HTTP headers for a request.
    ///
    /// Every Radix request requires three custom headers:
    /// <list type="bullet">
    ///   <item><c>Content-Type: Application/xml</c></item>
    ///   <item><c>USN-Code: {usnCode}</c> (Unique Station Number, 1-999999)</item>
    ///   <item><c>Operation: {operation}</c> (operation type code per Appendix B)</item>
    /// </list>
    ///
    /// Use the <see cref="OperationTransaction"/>, <see cref="OperationProducts"/>,
    /// <see cref="OperationAuthorize"/> etc. constants for the <paramref name="operation"/> parameter.
    /// </summary>
    /// <param name="usnCode">Unique Station Number configured for this FCC.</param>
    /// <param name="operation">Operation type code (e.g. "1" for transactions, "Authorize" for pre-auth).</param>
    /// <returns>Dictionary of header name to value.</returns>
    public static Dictionary<string, string> BuildHttpHeaders(int usnCode, string operation)
    {
        return new Dictionary<string, string>
        {
            ["Content-Type"] = "Application/xml",
            ["USN-Code"] = usnCode.ToString(),
            ["Operation"] = operation,
        };
    }

    // -----------------------------------------------------------------------
    // Private — HOST_REQ envelope builder
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a complete HOST_REQ XML with signed REQ content.
    ///
    /// Signing order:
    /// 1. Build <c>&lt;REQ&gt;...&lt;/REQ&gt;</c> string with CMD_CODE, CMD_NAME, TOKEN, and any extra elements
    /// 2. Compute SHA-1: <c>SHA1(&lt;REQ&gt;...&lt;/REQ&gt; + secret)</c>
    /// 3. Wrap in <c>&lt;HOST_REQ&gt;</c> with <c>&lt;SIGNATURE&gt;</c>
    /// </summary>
    private static string BuildHostReq(
        int cmdCode,
        string cmdName,
        string token,
        string secret,
        List<(string Tag, string Value)>? extraElements = null)
    {
        // Step 1: Build <REQ> content (this exact string is signed)
        var reqContent = BuildReqContent(cmdCode, cmdName, token, extraElements);

        // Step 2: Compute signature over exact <REQ> string
        var signature = RadixSignatureHelper.ComputeTransactionSignature(reqContent, secret);

        // Step 3: Wrap in HOST_REQ envelope
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<HOST_REQ>\n");
        sb.Append(reqContent).Append('\n');
        sb.Append("<SIGNATURE>").Append(signature).Append("</SIGNATURE>\n");
        sb.Append("</HOST_REQ>");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the <c>&lt;REQ&gt;...&lt;/REQ&gt;</c> element content.
    ///
    /// Format (newline + 4-space indent per child element):
    /// <code>
    /// &lt;REQ&gt;
    ///     &lt;CMD_CODE&gt;10&lt;/CMD_CODE&gt;
    ///     &lt;CMD_NAME&gt;TRN_REQ&lt;/CMD_NAME&gt;
    ///     &lt;TOKEN&gt;12345&lt;/TOKEN&gt;
    /// &lt;/REQ&gt;
    /// </code>
    /// </summary>
    private static string BuildReqContent(
        int cmdCode,
        string cmdName,
        string token,
        List<(string Tag, string Value)>? extraElements = null)
    {
        var sb = new StringBuilder();
        sb.Append("<REQ>\n");
        sb.Append("    <CMD_CODE>").Append(cmdCode).Append("</CMD_CODE>\n");
        sb.Append("    <CMD_NAME>").Append(cmdName).Append("</CMD_NAME>\n");
        sb.Append("    <TOKEN>").Append(token).Append("</TOKEN>\n");

        if (extraElements is not null)
        {
            foreach (var (tag, value) in extraElements)
            {
                sb.Append("    <").Append(tag).Append('>').Append(value).Append("</").Append(tag).Append(">\n");
            }
        }

        sb.Append("</REQ>");
        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Private — FDCMS envelope builder
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the <c>&lt;AUTH_DATA&gt;...&lt;/AUTH_DATA&gt;</c> element content from pre-auth params.
    ///
    /// Required fields are always present. Optional customer fields (CUSTNAME,
    /// CUSTIDTYPE, CUSTID, MOBILENUM, DISC_VALUE, DISC_TYPE) are only included
    /// when non-null.
    /// </summary>
    private static string BuildAuthDataContent(RadixPreAuthParams p)
    {
        var sb = new StringBuilder();
        sb.Append("<AUTH_DATA>\n");
        sb.Append("    <PUMP>").Append(p.Pump).Append("</PUMP>\n");
        sb.Append("    <FP>").Append(p.Fp).Append("</FP>\n");
        sb.Append("    <AUTH>").Append(p.Authorize ? "TRUE" : "FALSE").Append("</AUTH>\n");
        sb.Append("    <PROD>").Append(p.Product).Append("</PROD>\n");
        sb.Append("    <PRESET_VOLUME>").Append(p.PresetVolume).Append("</PRESET_VOLUME>\n");
        sb.Append("    <PRESET_AMOUNT>").Append(p.PresetAmount).Append("</PRESET_AMOUNT>\n");

        if (p.CustomerName is not null)
            sb.Append("    <CUSTNAME>").Append(EscapeXml(p.CustomerName)).Append("</CUSTNAME>\n");

        if (p.CustomerIdType is not null)
            sb.Append("    <CUSTIDTYPE>").Append(p.CustomerIdType.Value).Append("</CUSTIDTYPE>\n");

        if (p.CustomerId is not null)
            sb.Append("    <CUSTID>").Append(EscapeXml(p.CustomerId)).Append("</CUSTID>\n");

        if (p.MobileNumber is not null)
            sb.Append("    <MOBILENUM>").Append(EscapeXml(p.MobileNumber)).Append("</MOBILENUM>\n");

        if (p.DiscountValue is not null)
            sb.Append("    <DISC_VALUE>").Append(p.DiscountValue).Append("</DISC_VALUE>\n");

        if (p.DiscountType is not null)
            sb.Append("    <DISC_TYPE>").Append(p.DiscountType).Append("</DISC_TYPE>\n");

        sb.Append("    <TOKEN>").Append(p.Token).Append("</TOKEN>\n");
        sb.Append("</AUTH_DATA>");
        return sb.ToString();
    }

    /// <summary>
    /// Escapes XML special characters in user-provided string values.
    /// </summary>
    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    /// <summary>
    /// Wraps AUTH_DATA content and signature in the FDCMS envelope.
    /// </summary>
    private static string BuildFdcmsEnvelope(string authDataContent, string signature)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<FDCMS>\n");
        sb.Append(authDataContent).Append('\n');
        sb.Append("<FDCSIGNATURE>").Append(signature).Append("</FDCSIGNATURE>\n");
        sb.Append("</FDCMS>");
        return sb.ToString();
    }
}

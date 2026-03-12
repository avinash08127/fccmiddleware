package com.fccmiddleware.edge.adapter.radix

// ---------------------------------------------------------------------------
// Radix XML request body builder.
//
// Builds XML request bodies for Radix FCC operations:
//   - HOST_REQ envelope with SHA-1 signature (transaction management, port P+1)
//   - FDCMS envelope with SHA-1 signature (external authorization, port P)
//
// Builder methods:
//   - buildTransactionRequest     — CMD_CODE=10, TRN_REQ
//   - buildTransactionAck         — CMD_CODE=201, SUCCESS
//   - buildModeChangeRequest      — CMD_CODE=20, MODE_CHANGE with <MODE> element
//   - buildProductReadRequest     — CMD_CODE=55, PRODUCT_REQ
//   - buildPreAuthRequest         — <AUTH_DATA> with all RadixPreAuthParams fields
//   - buildPreAuthCancelRequest   — <AUTH_DATA> with <AUTH>FALSE</AUTH>
//   - buildHttpHeaders            — Custom Radix HTTP headers per Appendix B
//
// Critical signing order: Build inner content (<REQ> or <AUTH_DATA>) FIRST,
// compute SHA-1 via RadixSignatureHelper, THEN wrap in outer envelope.
// Whitespace in the signed content is locked before signing — do NOT
// reformat after computing the signature.
//
// XML is built with StringBuilder for character-exact control over output.
// ---------------------------------------------------------------------------

object RadixXmlBuilder {

    // -----------------------------------------------------------------------
    // Operation header constants (Appendix B)
    // -----------------------------------------------------------------------

    /** Operation header for transaction management (CMD_CODE 10, 20, 201). */
    const val OPERATION_TRANSACTION = "1"

    /** Operation header for products/prices read (CMD_CODE 55). */
    const val OPERATION_PRODUCTS = "2"

    /** Operation header for day close (CMD_CODE 77). */
    const val OPERATION_DAY_CLOSE = "3"

    /** Operation header for ATG data (CMD_CODE 30/35). */
    const val OPERATION_ATG = "4"

    /** Operation header for CSR data (CMD_CODE 40). */
    const val OPERATION_CSR = "5"

    /** Operation header for external authorization / pre-auth. */
    const val OPERATION_AUTHORIZE = "Authorize"

    // -----------------------------------------------------------------------
    // Transaction Management (Port P+1) — HOST_REQ envelope
    // -----------------------------------------------------------------------

    /**
     * Builds a transaction request (CMD_CODE=10, CMD_NAME=TRN_REQ).
     *
     * Requests the oldest unacknowledged transaction from the FCC's FIFO buffer.
     * The FCC responds with RESP_CODE=201 (transaction available) or
     * RESP_CODE=205 (buffer empty).
     *
     * @param token Request token for correlation
     * @param secret Shared secret password for SHA-1 signing
     * @return Complete HOST_REQ XML with signature
     */
    fun buildTransactionRequest(token: String, secret: String): String {
        return buildHostReq(cmdCode = 10, cmdName = "TRN_REQ", token = token, secret = secret)
    }

    /**
     * Builds a transaction acknowledgment (CMD_CODE=201, CMD_NAME=SUCCESS).
     *
     * Sent after receiving a transaction to dequeue it from the FCC's FIFO buffer.
     * Must be sent before requesting the next transaction.
     *
     * @param token Request token (should match the fetched transaction's token)
     * @param secret Shared secret password for SHA-1 signing
     * @return Complete HOST_REQ XML with signature
     */
    fun buildTransactionAck(token: String, secret: String): String {
        return buildHostReq(cmdCode = 201, cmdName = "SUCCESS", token = token, secret = secret)
    }

    /**
     * Builds a mode change request (CMD_CODE=20, CMD_NAME=MODE_CHANGE).
     *
     * Sets the transaction transfer mode on the FCC:
     * - 0 = OFF (transaction transfer disabled)
     * - 1 = ON_DEMAND (pull mode — host requests transactions)
     * - 2 = UNSOLICITED (push mode — FCC posts transactions automatically)
     *
     * Must be issued on adapter startup and after any FCC restart.
     *
     * @param mode Transaction mode (0=OFF, 1=ON_DEMAND, 2=UNSOLICITED)
     * @param token Request token for correlation
     * @param secret Shared secret password for SHA-1 signing
     * @return Complete HOST_REQ XML with signature
     */
    fun buildModeChangeRequest(mode: Int, token: String, secret: String): String {
        return buildHostReq(
            cmdCode = 20,
            cmdName = "MODE_CHANGE",
            token = token,
            secret = secret,
            extraElements = listOf("MODE" to mode.toString())
        )
    }

    /**
     * Builds a product read request (CMD_CODE=55, CMD_NAME=PRODUCT_REQ).
     *
     * Reads products and prices from the FCC. Also used as the heartbeat /
     * liveness probe since Radix has no dedicated heartbeat endpoint.
     *
     * @param token Request token for correlation
     * @param secret Shared secret password for SHA-1 signing
     * @return Complete HOST_REQ XML with signature
     */
    fun buildProductReadRequest(token: String, secret: String): String {
        return buildHostReq(cmdCode = 55, cmdName = "PRODUCT_REQ", token = token, secret = secret)
    }

    // -----------------------------------------------------------------------
    // External Authorization (Port P) — FDCMS envelope
    // -----------------------------------------------------------------------

    /**
     * Builds a pre-auth request with all fields from [params].
     *
     * Produces an `<FDCMS><AUTH_DATA>...</AUTH_DATA><FDCSIGNATURE>` XML body
     * for the authorization port (P). Optional customer fields (CUSTNAME,
     * CUSTIDTYPE, CUSTID, MOBILENUM, DISC_VALUE, DISC_TYPE) are included
     * only when non-null in [params].
     *
     * @param params Pre-auth parameters including pump, FP, product, presets, and optional customer data
     * @param secret Shared secret password for SHA-1 signing
     * @return Complete FDCMS XML with signature
     */
    fun buildPreAuthRequest(params: RadixPreAuthParams, secret: String): String {
        val authData = buildAuthDataContent(params)
        val signature = RadixSignatureHelper.computeAuthSignature(authData, secret)
        return buildFdcmsEnvelope(authData, signature)
    }

    /**
     * Builds a pre-auth cancellation request.
     *
     * Same FDCMS/AUTH_DATA structure as [buildPreAuthRequest] but with
     * `<AUTH>FALSE</AUTH>` to cancel an active authorization on the
     * specified pump/FP.
     *
     * @param pump DSB/RDG unit number
     * @param fp Filling point within the DSB/RDG
     * @param token Correlation token (0–65535)
     * @param secret Shared secret password for SHA-1 signing
     * @return Complete FDCMS XML with signature
     */
    fun buildPreAuthCancelRequest(pump: Int, fp: Int, token: String, secret: String): String {
        val params = RadixPreAuthParams(
            pump = pump,
            fp = fp,
            authorize = false,
            product = 0,
            presetVolume = "0.00",
            presetAmount = "0",
            token = token
        )
        return buildPreAuthRequest(params, secret)
    }

    // -----------------------------------------------------------------------
    // HTTP Headers (Appendix B)
    // -----------------------------------------------------------------------

    /**
     * Builds the required Radix HTTP headers for a request.
     *
     * Every Radix request requires three custom headers:
     * - `Content-Type: Application/xml`
     * - `USN-Code: {usnCode}` (Unique Station Number, 1–999999)
     * - `Operation: {operation}` (operation type code per Appendix B)
     *
     * Use the [OPERATION_TRANSACTION], [OPERATION_PRODUCTS], [OPERATION_AUTHORIZE]
     * etc. constants for the [operation] parameter.
     *
     * @param usnCode Unique Station Number configured for this FCC
     * @param operation Operation type code (e.g. "1" for transactions, "Authorize" for pre-auth)
     * @return Map of header name to value
     */
    fun buildHttpHeaders(usnCode: Int, operation: String): Map<String, String> {
        return mapOf(
            "Content-Type" to "Application/xml",
            "USN-Code" to usnCode.toString(),
            "Operation" to operation
        )
    }

    // -----------------------------------------------------------------------
    // Private — HOST_REQ envelope builder
    // -----------------------------------------------------------------------

    /**
     * Builds a complete HOST_REQ XML with signed REQ content.
     *
     * Signing order:
     * 1. Build `<REQ>...</REQ>` string with CMD_CODE, CMD_NAME, TOKEN, and any extra elements
     * 2. Compute SHA-1: `SHA1(<REQ>...</REQ> + secret)`
     * 3. Wrap in `<HOST_REQ>` with `<SIGNATURE>`
     */
    private fun buildHostReq(
        cmdCode: Int,
        cmdName: String,
        token: String,
        secret: String,
        extraElements: List<Pair<String, String>>? = null
    ): String {
        // Step 1: Build <REQ> content (this exact string is signed)
        val reqContent = buildReqContent(cmdCode, cmdName, token, extraElements)

        // Step 2: Compute signature over exact <REQ> string
        val signature = RadixSignatureHelper.computeTransactionSignature(reqContent, secret)

        // Step 3: Wrap in HOST_REQ envelope
        return StringBuilder().apply {
            append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            append("<HOST_REQ>\n")
            append(reqContent).append("\n")
            append("<SIGNATURE>").append(signature).append("</SIGNATURE>\n")
            append("</HOST_REQ>")
        }.toString()
    }

    /**
     * Builds the `<REQ>...</REQ>` element content.
     *
     * Format (newline + 4-space indent per child element):
     * ```
     * <REQ>
     *     <CMD_CODE>10</CMD_CODE>
     *     <CMD_NAME>TRN_REQ</CMD_NAME>
     *     <TOKEN>12345</TOKEN>
     * </REQ>
     * ```
     */
    private fun buildReqContent(
        cmdCode: Int,
        cmdName: String,
        token: String,
        extraElements: List<Pair<String, String>>? = null
    ): String {
        return StringBuilder().apply {
            append("<REQ>\n")
            append("    <CMD_CODE>").append(cmdCode).append("</CMD_CODE>\n")
            append("    <CMD_NAME>").append(cmdName).append("</CMD_NAME>\n")
            append("    <TOKEN>").append(token).append("</TOKEN>\n")
            extraElements?.forEach { (tag, value) ->
                append("    <").append(tag).append(">").append(value).append("</").append(tag).append(">\n")
            }
            append("</REQ>")
        }.toString()
    }

    // -----------------------------------------------------------------------
    // Private — FDCMS envelope builder
    // -----------------------------------------------------------------------

    /**
     * Builds the `<AUTH_DATA>...</AUTH_DATA>` element content from pre-auth params.
     *
     * Required fields are always present. Optional customer fields (CUSTNAME,
     * CUSTIDTYPE, CUSTID, MOBILENUM, DISC_VALUE, DISC_TYPE) are only included
     * when non-null.
     */
    private fun buildAuthDataContent(params: RadixPreAuthParams): String {
        return StringBuilder().apply {
            append("<AUTH_DATA>\n")
            append("    <PUMP>").append(params.pump).append("</PUMP>\n")
            append("    <FP>").append(params.fp).append("</FP>\n")
            append("    <AUTH>").append(if (params.authorize) "TRUE" else "FALSE").append("</AUTH>\n")
            append("    <PROD>").append(params.product).append("</PROD>\n")
            append("    <PRESET_VOLUME>").append(params.presetVolume).append("</PRESET_VOLUME>\n")
            append("    <PRESET_AMOUNT>").append(params.presetAmount).append("</PRESET_AMOUNT>\n")
            params.customerName?.let {
                append("    <CUSTNAME>").append(escapeXml(it)).append("</CUSTNAME>\n")
            }
            params.customerIdType?.let {
                append("    <CUSTIDTYPE>").append(escapeXml(it.toString())).append("</CUSTIDTYPE>\n")
            }
            params.customerId?.let {
                append("    <CUSTID>").append(escapeXml(it)).append("</CUSTID>\n")
            }
            params.mobileNumber?.let {
                append("    <MOBILENUM>").append(escapeXml(it)).append("</MOBILENUM>\n")
            }
            params.discountValue?.let {
                append("    <DISC_VALUE>").append(it).append("</DISC_VALUE>\n")
            }
            params.discountType?.let {
                append("    <DISC_TYPE>").append(it).append("</DISC_TYPE>\n")
            }
            append("    <TOKEN>").append(params.token).append("</TOKEN>\n")
            append("</AUTH_DATA>")
        }.toString()
    }

    /**
     * Escapes XML special characters in user-provided string values.
     *
     * Order matters: `&` must be replaced first to avoid double-encoding.
     */
    private fun escapeXml(value: String): String {
        return value
            .replace("&", "&amp;")
            .replace("<", "&lt;")
            .replace(">", "&gt;")
            .replace("\"", "&quot;")
            .replace("'", "&apos;")
    }

    /**
     * Wraps AUTH_DATA content and signature in the FDCMS envelope.
     */
    private fun buildFdcmsEnvelope(authDataContent: String, signature: String): String {
        return StringBuilder().apply {
            append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n")
            append("<FDCMS>\n")
            append(authDataContent).append("\n")
            append("<FDCSIGNATURE>").append(signature).append("</FDCSIGNATURE>\n")
            append("</FDCMS>")
        }.toString()
    }
}

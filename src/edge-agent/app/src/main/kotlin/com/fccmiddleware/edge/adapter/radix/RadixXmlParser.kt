package com.fccmiddleware.edge.adapter.radix

import org.w3c.dom.Document
import org.w3c.dom.Element
import java.io.ByteArrayInputStream
import javax.xml.parsers.DocumentBuilderFactory

/**
 * Sealed result type for Radix XML parsing operations.
 *
 * Used by [RadixXmlParser] to return either a successfully parsed
 * DTO or a descriptive error message for malformed/invalid XML.
 */
sealed class RadixParseResult<out T> {
    /** Successfully parsed response. */
    data class Success<T>(val value: T) : RadixParseResult<T>()
    /** Parse failure with a descriptive error message. */
    data class Error(val message: String) : RadixParseResult<Nothing>()
}

/**
 * Radix FDC XML response parser.
 *
 * Parses XML responses from the Radix FCC into typed DTOs:
 *   - Transaction responses (`<FDC_RESP>` with `<TRN>` data)
 *   - Auth/pre-auth acknowledgments (`<FDCMS>` with `<FDCACK>`)
 *   - Product list responses (`<FDC_RESP>` with `<PRODUCT>` elements)
 *
 * Also validates SHA-1 signatures on incoming responses using [RadixSignatureHelper].
 *
 * Uses [DocumentBuilderFactory] (Android standard library) for XML parsing.
 * Missing/empty XML attributes default to empty strings — no exceptions thrown
 * for absent optional data.
 */
object RadixXmlParser {

    // AP-012: Cache the DocumentBuilderFactory — it is thread-safe and its
    // service-provider lookup (1–3ms on Android) only needs to happen once.
    // A new DocumentBuilder is still created per parse call (builders are NOT thread-safe).
    private val docBuilderFactory: DocumentBuilderFactory = DocumentBuilderFactory.newInstance().apply {
        setFeature("http://xml.org/sax/features/external-general-entities", false)
        setFeature("http://xml.org/sax/features/external-parameter-entities", false)
    }

    // -----------------------------------------------------------------------
    // Public — Response parsing
    // -----------------------------------------------------------------------

    /**
     * Parses a transaction response (`<FDC_RESP>`) XML.
     *
     * Extracts `<ANS>` attributes (RESP_CODE, RESP_MSG, TOKEN) and optional
     * `<TRN>`, `<RFID_CARD>`, `<DISCOUNT>`, and `<CUST_DATA>` child elements.
     *
     * Response codes:
     * - 201: Success with transaction data
     * - 205: No transaction available (buffer empty)
     * - 30: Unsolicited push transaction
     * - 206, 251, 253, 255: Error codes
     *
     * @param xml Raw XML string from the FDC
     * @return [RadixParseResult.Success] with parsed [RadixTransactionResponse],
     *         or [RadixParseResult.Error] for malformed/invalid XML
     */
    fun parseTransactionResponse(xml: String): RadixParseResult<RadixTransactionResponse> {
        return try {
            val doc = parseXml(xml)

            val ansElement = firstElement(doc, "ANS")
                ?: return RadixParseResult.Error("Missing <ANS> element")

            val respCode = ansElement.getAttribute("RESP_CODE").trim().toIntOrNull()
                ?: return RadixParseResult.Error("Invalid or missing RESP_CODE")
            val respMsg = ansElement.attr("RESP_MSG")
            val token = ansElement.attr("TOKEN")

            val signature = firstElement(doc, "SIGNATURE")?.textContent?.trim() ?: ""

            // Parse child elements only when transaction data is expected
            val transaction = if (respCode == 201 || respCode == 30) {
                parseTrnElement(doc)
            } else {
                null
            }

            val rfidCard = parseRfidCardElement(doc)
            val discount = parseDiscountElement(doc)
            val customerData = parseCustDataElement(doc)

            RadixParseResult.Success(
                RadixTransactionResponse(
                    respCode = respCode,
                    respMsg = respMsg,
                    token = token,
                    transaction = transaction,
                    rfidCard = rfidCard,
                    discount = discount,
                    customerData = customerData,
                    signature = signature,
                )
            )
        } catch (e: Exception) {
            RadixParseResult.Error("Failed to parse transaction response: ${e.message}")
        }
    }

    /**
     * Parses an auth/pre-auth acknowledgment response (`<FDCMS>`) XML.
     *
     * Extracts `<FDCACK>` child elements: DATE, TIME, ACKCODE, ACKMSG,
     * and the `<FDCSIGNATURE>` value.
     *
     * Acknowledgment codes:
     * - 0: Success
     * - 251: Signature error
     * - 255: Bad XML format
     * - 256: Bad header format
     * - 258: Pump not ready
     * - 260: DSB offline
     *
     * @param xml Raw XML string from the FDC
     * @return [RadixParseResult.Success] with parsed [RadixAuthResponse],
     *         or [RadixParseResult.Error] for malformed/invalid XML
     */
    fun parseAuthResponse(xml: String): RadixParseResult<RadixAuthResponse> {
        return try {
            val doc = parseXml(xml)

            val fdcAck = firstElement(doc, "FDCACK")
                ?: return RadixParseResult.Error("Missing <FDCACK> element")

            val ackCode = childText(fdcAck, "ACKCODE").trim().toIntOrNull()
                ?: return RadixParseResult.Error("Invalid or missing ACKCODE")

            val signature = firstElement(doc, "FDCSIGNATURE")?.textContent?.trim() ?: ""

            RadixParseResult.Success(
                RadixAuthResponse(
                    date = childText(fdcAck, "DATE"),
                    time = childText(fdcAck, "TIME"),
                    ackCode = ackCode,
                    ackMsg = childText(fdcAck, "ACKMSG"),
                    signature = signature,
                )
            )
        } catch (e: Exception) {
            RadixParseResult.Error("Failed to parse auth response: ${e.message}")
        }
    }

    /**
     * Parses a product list response (`<FDC_RESP>` from CMD_CODE=55).
     *
     * Extracts `<ANS>` attributes and all `<PRODUCT>` child elements
     * with their ID, NAME, and PRICE attributes.
     *
     * @param xml Raw XML string from the FDC
     * @return [RadixParseResult.Success] with parsed [RadixProductResponse],
     *         or [RadixParseResult.Error] for malformed/invalid XML
     */
    fun parseProductResponse(xml: String): RadixParseResult<RadixProductResponse> {
        return try {
            val doc = parseXml(xml)

            val ansElement = firstElement(doc, "ANS")
                ?: return RadixParseResult.Error("Missing <ANS> element")

            val respCode = ansElement.getAttribute("RESP_CODE").trim().toIntOrNull()
                ?: return RadixParseResult.Error("Invalid or missing RESP_CODE")
            val respMsg = ansElement.attr("RESP_MSG")

            val products = mutableListOf<RadixProductData>()
            val productNodes = doc.getElementsByTagName("PRODUCT")
            for (i in 0 until productNodes.length) {
                val elem = productNodes.item(i) as Element
                val id = elem.getAttribute("ID").trim().toIntOrNull() ?: continue
                products.add(
                    RadixProductData(
                        id = id,
                        name = elem.attr("NAME"),
                        price = elem.attr("PRICE"),
                    )
                )
            }

            RadixParseResult.Success(
                RadixProductResponse(
                    respCode = respCode,
                    respMsg = respMsg,
                    products = products,
                )
            )
        } catch (e: Exception) {
            RadixParseResult.Error("Failed to parse product response: ${e.message}")
        }
    }

    // -----------------------------------------------------------------------
    // Public — Signature validation
    // -----------------------------------------------------------------------

    /**
     * Validates the SHA-1 signature of a transaction response (`<FDC_RESP>`).
     *
     * Extracts the raw `<TABLE>...</TABLE>` content from the XML string
     * (preserving exact whitespace for character-accurate hash) and validates
     * it against the `<SIGNATURE>` value using [RadixSignatureHelper].
     *
     * @param xml Raw XML string from the FDC
     * @param sharedSecret Shared secret password configured for this FCC
     * @return `true` if the signature is valid, `false` if invalid or extraction fails
     */
    fun validateTransactionResponseSignature(xml: String, sharedSecret: String): Boolean {
        val tableContent = extractRawElement(xml, "TABLE") ?: return false
        val signature = extractRawElementText(xml, "SIGNATURE") ?: return false
        return RadixSignatureHelper.validateSignature(tableContent, signature, sharedSecret)
    }

    /**
     * Validates the SHA-1 signature of an auth response (`<FDCMS>`).
     *
     * Extracts the raw `<FDCACK>...</FDCACK>` content from the XML string
     * and validates it against the `<FDCSIGNATURE>` value.
     *
     * @param xml Raw XML string from the FDC
     * @param sharedSecret Shared secret password configured for this FCC
     * @return `true` if the signature is valid, `false` if invalid or extraction fails
     */
    fun validateAuthResponseSignature(xml: String, sharedSecret: String): Boolean {
        val fdcAckContent = extractRawElement(xml, "FDCACK") ?: return false
        val signature = extractRawElementText(xml, "FDCSIGNATURE") ?: return false
        return RadixSignatureHelper.validateSignature(fdcAckContent, signature, sharedSecret)
    }

    // -----------------------------------------------------------------------
    // Private — XML DOM helpers
    // -----------------------------------------------------------------------

    /**
     * Parses an XML string into a DOM [Document].
     *
     * Disables external entity resolution to prevent XXE attacks.
     */
    private fun parseXml(xml: String): Document {
        val builder = docBuilderFactory.newDocumentBuilder()
        return builder.parse(ByteArrayInputStream(xml.toByteArray(Charsets.UTF_8)))
    }

    /** Returns the first element with [tagName], or null if not found. */
    private fun firstElement(doc: Document, tagName: String): Element? {
        val nodes = doc.getElementsByTagName(tagName)
        return if (nodes.length > 0) nodes.item(0) as Element else null
    }

    /** Returns the text content of the first child element with [tagName], or empty string. */
    private fun childText(parent: Element, tagName: String): String {
        val nodes = parent.getElementsByTagName(tagName)
        return if (nodes.length > 0) nodes.item(0).textContent ?: "" else ""
    }

    /** Safe attribute getter — returns empty string for absent attributes. */
    private fun Element.attr(name: String): String = getAttribute(name) ?: ""

    // -----------------------------------------------------------------------
    // Private — Element parsing
    // -----------------------------------------------------------------------

    private fun parseTrnElement(doc: Document): RadixTransactionData? {
        val trn = firstElement(doc, "TRN") ?: return null
        if (!trn.hasAttributes() && !trn.hasChildNodes()) return null

        // Try attribute first, fall back to child element text.
        // Some Radix firmware versions use child elements instead of attributes.
        fun field(name: String): String {
            val attrVal = trn.getAttribute(name)
            if (!attrVal.isNullOrEmpty()) return attrVal
            return childText(trn, name)
        }

        return RadixTransactionData(
            amo = field("AMO"),
            efdId = field("EFD_ID"),
            fdcDate = field("FDC_DATE"),
            fdcTime = field("FDC_TIME"),
            fdcName = field("FDC_NAME"),
            fdcNum = field("FDC_NUM"),
            fdcProd = field("FDC_PROD"),
            fdcProdName = field("FDC_PROD_NAME"),
            fdcSaveNum = field("FDC_SAVE_NUM"),
            fdcTank = field("FDC_TANK"),
            fp = field("FP"),
            noz = field("NOZ"),
            price = field("PRICE"),
            pumpAddr = field("PUMP_ADDR"),
            rdgDate = field("RDG_DATE"),
            rdgTime = field("RDG_TIME"),
            rdgId = field("RDG_ID"),
            rdgIndex = field("RDG_INDEX"),
            rdgProd = field("RDG_PROD"),
            rdgSaveNum = field("RDG_SAVE_NUM"),
            regId = field("REG_ID"),
            roundType = field("ROUND_TYPE"),
            vol = field("VOL"),
        )
    }

    private fun parseRfidCardElement(doc: Document): RadixRfidCardData? {
        val elem = firstElement(doc, "RFID_CARD") ?: return null
        if (!elem.hasAttributes()) return null

        return RadixRfidCardData(
            cardType = elem.attr("CARD_TYPE"),
            custContact = elem.attr("CUST_CONTACT"),
            custId = elem.attr("CUST_ID"),
            custIdType = elem.attr("CUST_IDTYPE"),
            custName = elem.attr("CUST_NAME"),
            discount = elem.attr("DISCOUNT"),
            discountType = elem.attr("DISCOUNT_TYPE"),
            num = elem.attr("NUM"),
            num10 = elem.attr("NUM_10"),
            payMethod = elem.attr("PAY_METHOD"),
            productEnabled = elem.attr("PRODUCT_ENABLED"),
            used = elem.attr("USED"),
        )
    }

    private fun parseDiscountElement(doc: Document): RadixDiscountData? {
        val elem = firstElement(doc, "DISCOUNT") ?: return null
        if (!elem.hasAttributes()) return null

        return RadixDiscountData(
            amoDiscount = elem.attr("AMO_DISCOUNT"),
            amoNew = elem.attr("AMO_NEW"),
            amoOrigin = elem.attr("AMO_ORIGIN"),
            discountType = elem.attr("DISCOUNT_TYPE"),
            priceDiscount = elem.attr("PRICE_DISCOUNT"),
            priceNew = elem.attr("PRICE_NEW"),
            priceOrigin = elem.attr("PRICE_ORIGIN"),
            volOrigin = elem.attr("VOL_ORIGIN"),
        )
    }

    private fun parseCustDataElement(doc: Document): RadixCustomerData? {
        val elem = firstElement(doc, "CUST_DATA") ?: return null
        if (!elem.hasAttributes()) return null

        val used = elem.getAttribute("USED").trim().toIntOrNull() ?: return null
        return RadixCustomerData(used = used)
    }

    // -----------------------------------------------------------------------
    // Private — Raw XML extraction for signature validation
    // -----------------------------------------------------------------------

    /**
     * Extracts raw element text including its tags from the XML string.
     *
     * Returns the substring from `<tagName` through `</tagName>` inclusive,
     * preserving exact whitespace for signature validation.
     */
    private fun extractRawElement(xml: String, tagName: String): String? {
        val openTag = "<$tagName"
        val closeTag = "</$tagName>"
        val startIdx = xml.indexOf(openTag)
        if (startIdx < 0) return null
        val endIdx = xml.indexOf(closeTag, startIdx)
        if (endIdx < 0) return null
        return xml.substring(startIdx, endIdx + closeTag.length)
    }

    /**
     * Extracts the text content between `<tagName>` and `</tagName>`,
     * trimming whitespace.
     */
    private fun extractRawElementText(xml: String, tagName: String): String? {
        val openTag = "<$tagName>"
        val closeTag = "</$tagName>"
        val startIdx = xml.indexOf(openTag)
        if (startIdx < 0) return null
        val endIdx = xml.indexOf(closeTag, startIdx)
        if (endIdx < 0) return null
        return xml.substring(startIdx + openTag.length, endIdx).trim()
    }
}

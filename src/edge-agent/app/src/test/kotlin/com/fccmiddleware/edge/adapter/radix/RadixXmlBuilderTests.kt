package com.fccmiddleware.edge.adapter.radix

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Unit tests for [RadixXmlBuilder].
 *
 * Covers:
 *   - HOST_REQ envelope construction with signature (transaction management, port P+1)
 *   - CMD_CODE=10 transaction request XML
 *   - CMD_CODE=201 transaction ACK XML
 *   - CMD_CODE=20 mode management XML (OFF=0, ON_DEMAND=1, UNSOLICITED=2)
 *   - CMD_CODE=55 product read request XML
 *   - FDCMS/AUTH_DATA pre-auth XML construction with all optional fields
 *   - Pre-auth cancel with AUTH=FALSE
 *   - HTTP header generation per Appendix B
 *   - Round-trip: build XML → extract inner content → recompute signature → matches
 *   - Well-formed XML for all builder methods
 *
 * Test framework: JUnit (AndroidJUnit4 runner compatible)
 */
class RadixXmlBuilderTests {

    companion object {
        private const val SECRET = "MySecretPassword"
        private const val TOKEN = "12345"
    }

    // -----------------------------------------------------------------------
    // Transaction Request (CMD_CODE=10)
    // -----------------------------------------------------------------------

    @Test
    fun `buildTransactionRequest produces well-formed XML with HOST_REQ envelope`() {
        val xml = RadixXmlBuilder.buildTransactionRequest(TOKEN, SECRET)
        assertTrue("Must start with XML declaration", xml.startsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>"))
        assertTrue("Must contain HOST_REQ root", xml.contains("<HOST_REQ>"))
        assertTrue("Must close HOST_REQ", xml.endsWith("</HOST_REQ>"))
    }

    @Test
    fun `buildTransactionRequest has correct CMD_CODE and CMD_NAME`() {
        val xml = RadixXmlBuilder.buildTransactionRequest(TOKEN, SECRET)
        assertTrue("CMD_CODE must be 10", xml.contains("<CMD_CODE>10</CMD_CODE>"))
        assertTrue("CMD_NAME must be TRN_REQ", xml.contains("<CMD_NAME>TRN_REQ</CMD_NAME>"))
    }

    @Test
    fun `buildTransactionRequest includes TOKEN`() {
        val xml = RadixXmlBuilder.buildTransactionRequest("99999", SECRET)
        assertTrue("Must contain TOKEN element", xml.contains("<TOKEN>99999</TOKEN>"))
    }

    @Test
    fun `buildTransactionRequest has SIGNATURE element that is non-empty`() {
        val xml = RadixXmlBuilder.buildTransactionRequest(TOKEN, SECRET)
        val sigMatch = Regex("<SIGNATURE>([a-f0-9]{40})</SIGNATURE>").find(xml)
        assertNotNull("SIGNATURE element must be present with 40-char hex", sigMatch)
    }

    // -----------------------------------------------------------------------
    // Transaction ACK (CMD_CODE=201)
    // -----------------------------------------------------------------------

    @Test
    fun `buildTransactionAck produces well-formed XML with HOST_REQ envelope`() {
        val xml = RadixXmlBuilder.buildTransactionAck(TOKEN, SECRET)
        assertTrue(xml.startsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>"))
        assertTrue(xml.contains("<HOST_REQ>"))
        assertTrue(xml.endsWith("</HOST_REQ>"))
    }

    @Test
    fun `buildTransactionAck has correct CMD_CODE and CMD_NAME`() {
        val xml = RadixXmlBuilder.buildTransactionAck(TOKEN, SECRET)
        assertTrue("CMD_CODE must be 201", xml.contains("<CMD_CODE>201</CMD_CODE>"))
        assertTrue("CMD_NAME must be SUCCESS", xml.contains("<CMD_NAME>SUCCESS</CMD_NAME>"))
    }

    @Test
    fun `buildTransactionAck has SIGNATURE element that is non-empty`() {
        val xml = RadixXmlBuilder.buildTransactionAck(TOKEN, SECRET)
        val sigMatch = Regex("<SIGNATURE>([a-f0-9]{40})</SIGNATURE>").find(xml)
        assertNotNull("SIGNATURE must be present", sigMatch)
    }

    // -----------------------------------------------------------------------
    // Mode Change (CMD_CODE=20)
    // -----------------------------------------------------------------------

    @Test
    fun `buildModeChangeRequest produces well-formed XML with HOST_REQ envelope`() {
        val xml = RadixXmlBuilder.buildModeChangeRequest(1, TOKEN, SECRET)
        assertTrue(xml.startsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>"))
        assertTrue(xml.contains("<HOST_REQ>"))
        assertTrue(xml.endsWith("</HOST_REQ>"))
    }

    @Test
    fun `buildModeChangeRequest has correct CMD_CODE and CMD_NAME`() {
        val xml = RadixXmlBuilder.buildModeChangeRequest(1, TOKEN, SECRET)
        assertTrue("CMD_CODE must be 20", xml.contains("<CMD_CODE>20</CMD_CODE>"))
        assertTrue("CMD_NAME must be MODE_CHANGE", xml.contains("<CMD_NAME>MODE_CHANGE</CMD_NAME>"))
    }

    @Test
    fun `buildModeChangeRequest includes MODE element with OFF value 0`() {
        val xml = RadixXmlBuilder.buildModeChangeRequest(0, TOKEN, SECRET)
        assertTrue("MODE must be 0 for OFF", xml.contains("<MODE>0</MODE>"))
    }

    @Test
    fun `buildModeChangeRequest includes MODE element with ON_DEMAND value 1`() {
        val xml = RadixXmlBuilder.buildModeChangeRequest(1, TOKEN, SECRET)
        assertTrue("MODE must be 1 for ON_DEMAND", xml.contains("<MODE>1</MODE>"))
    }

    @Test
    fun `buildModeChangeRequest includes MODE element with UNSOLICITED value 2`() {
        val xml = RadixXmlBuilder.buildModeChangeRequest(2, TOKEN, SECRET)
        assertTrue("MODE must be 2 for UNSOLICITED", xml.contains("<MODE>2</MODE>"))
    }

    @Test
    fun `buildModeChangeRequest has SIGNATURE element`() {
        val xml = RadixXmlBuilder.buildModeChangeRequest(1, TOKEN, SECRET)
        val sigMatch = Regex("<SIGNATURE>([a-f0-9]{40})</SIGNATURE>").find(xml)
        assertNotNull("SIGNATURE must be present", sigMatch)
    }

    @Test
    fun `buildModeChangeRequest different modes produce different signatures`() {
        val xml0 = RadixXmlBuilder.buildModeChangeRequest(0, TOKEN, SECRET)
        val xml1 = RadixXmlBuilder.buildModeChangeRequest(1, TOKEN, SECRET)
        val xml2 = RadixXmlBuilder.buildModeChangeRequest(2, TOKEN, SECRET)

        val sig0 = extractSignature(xml0)
        val sig1 = extractSignature(xml1)
        val sig2 = extractSignature(xml2)

        assertTrue("Mode 0 and 1 must produce different signatures", sig0 != sig1)
        assertTrue("Mode 1 and 2 must produce different signatures", sig1 != sig2)
        assertTrue("Mode 0 and 2 must produce different signatures", sig0 != sig2)
    }

    // -----------------------------------------------------------------------
    // Product Read (CMD_CODE=55)
    // -----------------------------------------------------------------------

    @Test
    fun `buildProductReadRequest produces well-formed XML with HOST_REQ envelope`() {
        val xml = RadixXmlBuilder.buildProductReadRequest(TOKEN, SECRET)
        assertTrue(xml.startsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>"))
        assertTrue(xml.contains("<HOST_REQ>"))
        assertTrue(xml.endsWith("</HOST_REQ>"))
    }

    @Test
    fun `buildProductReadRequest has correct CMD_CODE and CMD_NAME`() {
        val xml = RadixXmlBuilder.buildProductReadRequest(TOKEN, SECRET)
        assertTrue("CMD_CODE must be 55", xml.contains("<CMD_CODE>55</CMD_CODE>"))
        assertTrue("CMD_NAME must be PRODUCT_REQ", xml.contains("<CMD_NAME>PRODUCT_REQ</CMD_NAME>"))
    }

    @Test
    fun `buildProductReadRequest has SIGNATURE element`() {
        val xml = RadixXmlBuilder.buildProductReadRequest(TOKEN, SECRET)
        val sigMatch = Regex("<SIGNATURE>([a-f0-9]{40})</SIGNATURE>").find(xml)
        assertNotNull("SIGNATURE must be present", sigMatch)
    }

    // -----------------------------------------------------------------------
    // Pre-Auth Request (FDCMS / AUTH_DATA)
    // -----------------------------------------------------------------------

    @Test
    fun `buildPreAuthRequest produces well-formed XML with FDCMS envelope`() {
        val params = minimalPreAuthParams()
        val xml = RadixXmlBuilder.buildPreAuthRequest(params, SECRET)
        assertTrue("Must start with XML declaration", xml.startsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>"))
        assertTrue("Must contain FDCMS root", xml.contains("<FDCMS>"))
        assertTrue("Must close FDCMS", xml.endsWith("</FDCMS>"))
    }

    @Test
    fun `buildPreAuthRequest includes all required AUTH_DATA fields`() {
        val params = RadixPreAuthParams(
            pump = 3,
            fp = 1,
            authorize = true,
            product = 2,
            presetVolume = "0.00",
            presetAmount = "2000",
            token = "123456"
        )
        val xml = RadixXmlBuilder.buildPreAuthRequest(params, SECRET)

        assertTrue("Must contain PUMP", xml.contains("<PUMP>3</PUMP>"))
        assertTrue("Must contain FP", xml.contains("<FP>1</FP>"))
        assertTrue("Must contain AUTH=TRUE", xml.contains("<AUTH>TRUE</AUTH>"))
        assertTrue("Must contain PROD", xml.contains("<PROD>2</PROD>"))
        assertTrue("Must contain PRESET_VOLUME", xml.contains("<PRESET_VOLUME>0.00</PRESET_VOLUME>"))
        assertTrue("Must contain PRESET_AMOUNT", xml.contains("<PRESET_AMOUNT>2000</PRESET_AMOUNT>"))
        assertTrue("Must contain TOKEN", xml.contains("<TOKEN>123456</TOKEN>"))
    }

    @Test
    fun `buildPreAuthRequest includes all non-null optional customer fields`() {
        val params = RadixPreAuthParams(
            pump = 3,
            fp = 1,
            authorize = true,
            product = 2,
            presetVolume = "0.00",
            presetAmount = "2000",
            customerName = "TECHOLOGY Ltd.",
            customerIdType = 1,
            customerId = "12345678",
            mobileNumber = "25588776655",
            discountValue = 10,
            discountType = "VALUE",
            token = "123456"
        )
        val xml = RadixXmlBuilder.buildPreAuthRequest(params, SECRET)

        assertTrue("Must contain CUSTNAME", xml.contains("<CUSTNAME>TECHOLOGY Ltd.</CUSTNAME>"))
        assertTrue("Must contain CUSTIDTYPE", xml.contains("<CUSTIDTYPE>1</CUSTIDTYPE>"))
        assertTrue("Must contain CUSTID", xml.contains("<CUSTID>12345678</CUSTID>"))
        assertTrue("Must contain MOBILENUM", xml.contains("<MOBILENUM>25588776655</MOBILENUM>"))
        assertTrue("Must contain DISC_VALUE", xml.contains("<DISC_VALUE>10</DISC_VALUE>"))
        assertTrue("Must contain DISC_TYPE", xml.contains("<DISC_TYPE>VALUE</DISC_TYPE>"))
    }

    @Test
    fun `buildPreAuthRequest omits null optional customer fields`() {
        val params = minimalPreAuthParams()
        val xml = RadixXmlBuilder.buildPreAuthRequest(params, SECRET)

        assertFalse("Must not contain CUSTNAME when null", xml.contains("<CUSTNAME>"))
        assertFalse("Must not contain CUSTIDTYPE when null", xml.contains("<CUSTIDTYPE>"))
        assertFalse("Must not contain CUSTID when null", xml.contains("<CUSTID>"))
        assertFalse("Must not contain MOBILENUM when null", xml.contains("<MOBILENUM>"))
        assertFalse("Must not contain DISC_VALUE when null", xml.contains("<DISC_VALUE>"))
        assertFalse("Must not contain DISC_TYPE when null", xml.contains("<DISC_TYPE>"))
    }

    @Test
    fun `buildPreAuthRequest has FDCSIGNATURE element that is non-empty`() {
        val params = minimalPreAuthParams()
        val xml = RadixXmlBuilder.buildPreAuthRequest(params, SECRET)
        val sigMatch = Regex("<FDCSIGNATURE>([a-f0-9]{40})</FDCSIGNATURE>").find(xml)
        assertNotNull("FDCSIGNATURE must be present with 40-char hex", sigMatch)
    }

    @Test
    fun `buildPreAuthRequest uses AUTH_DATA tags not REQ tags`() {
        val params = minimalPreAuthParams()
        val xml = RadixXmlBuilder.buildPreAuthRequest(params, SECRET)

        assertTrue("Must contain AUTH_DATA opening tag", xml.contains("<AUTH_DATA>"))
        assertTrue("Must contain AUTH_DATA closing tag", xml.contains("</AUTH_DATA>"))
        assertFalse("Must NOT contain REQ tags (pre-auth uses FDCMS)", xml.contains("<REQ>"))
        assertFalse("Must NOT contain SIGNATURE (pre-auth uses FDCSIGNATURE)", xml.contains("<SIGNATURE>"))
    }

    // -----------------------------------------------------------------------
    // Pre-Auth Cancel
    // -----------------------------------------------------------------------

    @Test
    fun `buildPreAuthCancelRequest has AUTH FALSE`() {
        val xml = RadixXmlBuilder.buildPreAuthCancelRequest(pump = 3, fp = 1, token = "999", secret = SECRET)
        assertTrue("Cancel must have AUTH=FALSE", xml.contains("<AUTH>FALSE</AUTH>"))
    }

    @Test
    fun `buildPreAuthCancelRequest produces FDCMS envelope`() {
        val xml = RadixXmlBuilder.buildPreAuthCancelRequest(pump = 3, fp = 1, token = "999", secret = SECRET)
        assertTrue(xml.contains("<FDCMS>"))
        assertTrue(xml.contains("<AUTH_DATA>"))
        assertTrue(xml.endsWith("</FDCMS>"))
    }

    @Test
    fun `buildPreAuthCancelRequest includes pump and fp`() {
        val xml = RadixXmlBuilder.buildPreAuthCancelRequest(pump = 5, fp = 2, token = "100", secret = SECRET)
        assertTrue("Must contain PUMP", xml.contains("<PUMP>5</PUMP>"))
        assertTrue("Must contain FP", xml.contains("<FP>2</FP>"))
    }

    @Test
    fun `buildPreAuthCancelRequest has FDCSIGNATURE`() {
        val xml = RadixXmlBuilder.buildPreAuthCancelRequest(pump = 1, fp = 0, token = "50", secret = SECRET)
        val sigMatch = Regex("<FDCSIGNATURE>([a-f0-9]{40})</FDCSIGNATURE>").find(xml)
        assertNotNull("Cancel request must have FDCSIGNATURE", sigMatch)
    }

    @Test
    fun `buildPreAuthCancelRequest omits optional customer fields`() {
        val xml = RadixXmlBuilder.buildPreAuthCancelRequest(pump = 1, fp = 0, token = "50", secret = SECRET)
        assertFalse("Cancel must not contain CUSTNAME", xml.contains("<CUSTNAME>"))
        assertFalse("Cancel must not contain CUSTIDTYPE", xml.contains("<CUSTIDTYPE>"))
        assertFalse("Cancel must not contain CUSTID", xml.contains("<CUSTID>"))
        assertFalse("Cancel must not contain MOBILENUM", xml.contains("<MOBILENUM>"))
    }

    // -----------------------------------------------------------------------
    // HTTP Headers
    // -----------------------------------------------------------------------

    @Test
    fun `buildHttpHeaders returns correct Content-Type`() {
        val headers = RadixXmlBuilder.buildHttpHeaders(100, RadixXmlBuilder.OPERATION_TRANSACTION)
        assertEquals("Application/xml", headers["Content-Type"])
    }

    @Test
    fun `buildHttpHeaders returns USN-Code as string`() {
        val headers = RadixXmlBuilder.buildHttpHeaders(999999, RadixXmlBuilder.OPERATION_TRANSACTION)
        assertEquals("999999", headers["USN-Code"])
    }

    @Test
    fun `buildHttpHeaders returns correct Operation for transaction management`() {
        val headers = RadixXmlBuilder.buildHttpHeaders(100, RadixXmlBuilder.OPERATION_TRANSACTION)
        assertEquals("1", headers["Operation"])
    }

    @Test
    fun `buildHttpHeaders returns correct Operation for products`() {
        val headers = RadixXmlBuilder.buildHttpHeaders(100, RadixXmlBuilder.OPERATION_PRODUCTS)
        assertEquals("2", headers["Operation"])
    }

    @Test
    fun `buildHttpHeaders returns correct Operation for authorize`() {
        val headers = RadixXmlBuilder.buildHttpHeaders(100, RadixXmlBuilder.OPERATION_AUTHORIZE)
        assertEquals("Authorize", headers["Operation"])
    }

    @Test
    fun `buildHttpHeaders contains exactly three entries`() {
        val headers = RadixXmlBuilder.buildHttpHeaders(1, "1")
        assertEquals(3, headers.size)
    }

    // -----------------------------------------------------------------------
    // Round-trip: build XML → extract inner content → recompute signature
    // -----------------------------------------------------------------------

    @Test
    fun `round-trip transaction request - extracted REQ content recomputes to same signature`() {
        val xml = RadixXmlBuilder.buildTransactionRequest(TOKEN, SECRET)

        val reqContent = extractReqContent(xml)
        val embeddedSig = extractSignature(xml)
        val recomputedSig = RadixSignatureHelper.computeTransactionSignature(reqContent, SECRET)

        assertEquals("Recomputed signature must match embedded SIGNATURE", embeddedSig, recomputedSig)
    }

    @Test
    fun `round-trip transaction ack - extracted REQ content recomputes to same signature`() {
        val xml = RadixXmlBuilder.buildTransactionAck(TOKEN, SECRET)

        val reqContent = extractReqContent(xml)
        val embeddedSig = extractSignature(xml)
        val recomputedSig = RadixSignatureHelper.computeTransactionSignature(reqContent, SECRET)

        assertEquals("Recomputed signature must match embedded SIGNATURE", embeddedSig, recomputedSig)
    }

    @Test
    fun `round-trip mode change - extracted REQ content recomputes to same signature`() {
        val xml = RadixXmlBuilder.buildModeChangeRequest(2, TOKEN, SECRET)

        val reqContent = extractReqContent(xml)
        val embeddedSig = extractSignature(xml)
        val recomputedSig = RadixSignatureHelper.computeTransactionSignature(reqContent, SECRET)

        assertEquals("Recomputed signature must match embedded SIGNATURE", embeddedSig, recomputedSig)
    }

    @Test
    fun `round-trip product read - extracted REQ content recomputes to same signature`() {
        val xml = RadixXmlBuilder.buildProductReadRequest(TOKEN, SECRET)

        val reqContent = extractReqContent(xml)
        val embeddedSig = extractSignature(xml)
        val recomputedSig = RadixSignatureHelper.computeTransactionSignature(reqContent, SECRET)

        assertEquals("Recomputed signature must match embedded SIGNATURE", embeddedSig, recomputedSig)
    }

    @Test
    fun `round-trip pre-auth request - extracted AUTH_DATA content recomputes to same signature`() {
        val params = RadixPreAuthParams(
            pump = 3,
            fp = 1,
            authorize = true,
            product = 2,
            presetVolume = "0.00",
            presetAmount = "2000",
            customerName = "TECHOLOGY Ltd.",
            customerIdType = 1,
            customerId = "12345678",
            mobileNumber = "25588776655",
            discountValue = 10,
            discountType = "VALUE",
            token = "123456"
        )
        val xml = RadixXmlBuilder.buildPreAuthRequest(params, SECRET)

        val authDataContent = extractAuthDataContent(xml)
        val embeddedSig = extractFdcSignature(xml)
        val recomputedSig = RadixSignatureHelper.computeAuthSignature(authDataContent, SECRET)

        assertEquals("Recomputed signature must match embedded FDCSIGNATURE", embeddedSig, recomputedSig)
    }

    @Test
    fun `round-trip pre-auth cancel - extracted AUTH_DATA content recomputes to same signature`() {
        val xml = RadixXmlBuilder.buildPreAuthCancelRequest(pump = 3, fp = 1, token = "999", secret = SECRET)

        val authDataContent = extractAuthDataContent(xml)
        val embeddedSig = extractFdcSignature(xml)
        val recomputedSig = RadixSignatureHelper.computeAuthSignature(authDataContent, SECRET)

        assertEquals("Recomputed signature must match embedded FDCSIGNATURE", embeddedSig, recomputedSig)
    }

    // -----------------------------------------------------------------------
    // Determinism — same inputs produce identical output
    // -----------------------------------------------------------------------

    @Test
    fun `repeated calls with same inputs produce identical XML`() {
        val xml1 = RadixXmlBuilder.buildTransactionRequest(TOKEN, SECRET)
        val xml2 = RadixXmlBuilder.buildTransactionRequest(TOKEN, SECRET)
        assertEquals("Builder must be deterministic", xml1, xml2)
    }

    @Test
    fun `different tokens produce different XML and signatures`() {
        val xml1 = RadixXmlBuilder.buildTransactionRequest("11111", SECRET)
        val xml2 = RadixXmlBuilder.buildTransactionRequest("22222", SECRET)
        assertTrue("Different tokens must produce different XML", xml1 != xml2)
        assertTrue("Different tokens must produce different signatures", extractSignature(xml1) != extractSignature(xml2))
    }

    @Test
    fun `different secrets produce different signatures but same structure`() {
        val xml1 = RadixXmlBuilder.buildTransactionRequest(TOKEN, "secret1")
        val xml2 = RadixXmlBuilder.buildTransactionRequest(TOKEN, "secret2")
        assertTrue("Different secrets must produce different signatures", extractSignature(xml1) != extractSignature(xml2))
        // Structure (everything except signature) should be the same
        val struct1 = xml1.replace(Regex("<SIGNATURE>[a-f0-9]{40}</SIGNATURE>"), "<SIGNATURE>X</SIGNATURE>")
        val struct2 = xml2.replace(Regex("<SIGNATURE>[a-f0-9]{40}</SIGNATURE>"), "<SIGNATURE>X</SIGNATURE>")
        assertEquals("Structure must be identical regardless of secret", struct1, struct2)
    }

    // -----------------------------------------------------------------------
    // XML encoding
    // -----------------------------------------------------------------------

    @Test
    fun `HOST_REQ envelope uses UTF-8 encoding declaration`() {
        val xml = RadixXmlBuilder.buildTransactionRequest(TOKEN, SECRET)
        assertTrue(xml.contains("encoding=\"UTF-8\""))
    }

    @Test
    fun `FDCMS envelope uses utf-8 encoding declaration`() {
        val params = minimalPreAuthParams()
        val xml = RadixXmlBuilder.buildPreAuthRequest(params, SECRET)
        assertTrue(xml.contains("encoding=\"utf-8\""))
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private fun minimalPreAuthParams(): RadixPreAuthParams = RadixPreAuthParams(
        pump = 1,
        fp = 0,
        authorize = true,
        product = 0,
        presetVolume = "0.00",
        presetAmount = "5000",
        token = "100"
    )

    private fun extractSignature(xml: String): String {
        val match = Regex("<SIGNATURE>([a-f0-9]{40})</SIGNATURE>").find(xml)
        return match?.groupValues?.get(1) ?: throw AssertionError("No SIGNATURE found in XML")
    }

    private fun extractFdcSignature(xml: String): String {
        val match = Regex("<FDCSIGNATURE>([a-f0-9]{40})</FDCSIGNATURE>").find(xml)
        return match?.groupValues?.get(1) ?: throw AssertionError("No FDCSIGNATURE found in XML")
    }

    private fun extractReqContent(xml: String): String {
        val start = xml.indexOf("<REQ>")
        val end = xml.indexOf("</REQ>") + "</REQ>".length
        if (start < 0 || end <= 0) throw AssertionError("No <REQ>...</REQ> found in XML")
        return xml.substring(start, end)
    }

    private fun extractAuthDataContent(xml: String): String {
        val start = xml.indexOf("<AUTH_DATA>")
        val end = xml.indexOf("</AUTH_DATA>") + "</AUTH_DATA>".length
        if (start < 0 || end <= 0) throw AssertionError("No <AUTH_DATA>...</AUTH_DATA> found in XML")
        return xml.substring(start, end)
    }
}

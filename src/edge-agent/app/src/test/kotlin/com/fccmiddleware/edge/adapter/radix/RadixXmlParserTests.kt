package com.fccmiddleware.edge.adapter.radix

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Unit tests for [RadixXmlParser].
 *
 * Covers:
 *   - Parsing `<TRN>` elements from `<FDC_RESP>` (all field variants)
 *   - Parsing `<FDCACK>` pre-auth responses (ACKCODE, DATE, TIME, ACKMSG)
 *   - RESP_CODE handling: 201 (success), 205 (buffer empty), 30 (unsolicited), error codes
 *   - Edge cases: empty elements, missing attributes, malformed XML
 *   - Product list (CMD_CODE=55) response parsing
 *   - Signature validation for transaction and auth responses
 *
 * Test fixtures in: src/test/resources/fixtures/
 * Test framework: JUnit 4 (AndroidJUnit4 runner compatible)
 */
class RadixXmlParserTests {

    companion object {
        private const val SECRET = "MySecretPassword"

        /** Loads a fixture XML file from test resources. */
        private fun loadFixture(name: String): String {
            val stream = RadixXmlParserTests::class.java.classLoader
                ?.getResourceAsStream("fixtures/$name")
                ?: throw IllegalStateException("Fixture not found: $name")
            return stream.bufferedReader().readText()
        }
    }

    // -----------------------------------------------------------------------
    // Transaction response — Success (RESP_CODE=201)
    // -----------------------------------------------------------------------

    @Test
    fun `parseTransactionResponse success - all TRN attributes populated correctly`() {
        val xml = loadFixture("transaction-success.xml")
        val result = RadixXmlParser.parseTransactionResponse(xml)

        assertTrue("Parse must succeed", result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value

        assertEquals(201, response.respCode)
        assertEquals("SUCCESS", response.respMsg)
        assertEquals("12345", response.token)

        val trn = response.transaction
        assertNotNull("Transaction must not be null for RESP_CODE=201", trn)
        trn!!

        assertEquals("30000.0", trn.amo)
        assertEquals("182AC9368989", trn.efdId)
        assertEquals("2021-03-03", trn.fdcDate)
        assertEquals("21:17:53", trn.fdcTime)
        assertEquals("10TZ100449", trn.fdcName)
        assertEquals("100253410", trn.fdcNum)
        assertEquals("0", trn.fdcProd)
        assertEquals("UNLEADED", trn.fdcProdName)
        assertEquals("368989", trn.fdcSaveNum)
        assertEquals("", trn.fdcTank)
        assertEquals("0", trn.fp)
        assertEquals("0", trn.noz)
        assertEquals("1930", trn.price)
        assertEquals("0", trn.pumpAddr)
        assertEquals("2021-03-03", trn.rdgDate)
        assertEquals("21:17:53", trn.rdgTime)
        assertEquals("0", trn.rdgId)
        assertEquals("0", trn.rdgIndex)
        assertEquals("0", trn.rdgProd)
        assertEquals("1066", trn.rdgSaveNum)
        assertEquals("TZ0100551361", trn.regId)
        assertEquals("0", trn.roundType)
        assertEquals("15.54", trn.vol)
    }

    @Test
    fun `parseTransactionResponse success - RFID card parsed with USED attribute`() {
        val xml = loadFixture("transaction-success.xml")
        val result = RadixXmlParser.parseTransactionResponse(xml)
        val response = (result as RadixParseResult.Success).value

        assertNotNull("RFID card must be parsed when USED attribute present", response.rfidCard)
        assertEquals("0", response.rfidCard!!.used)
    }

    @Test
    fun `parseTransactionResponse success - discount element parsed with empty attributes`() {
        val xml = loadFixture("transaction-success.xml")
        val result = RadixXmlParser.parseTransactionResponse(xml)
        val response = (result as RadixParseResult.Success).value

        assertNotNull("Discount must be parsed when attributes present", response.discount)
        assertEquals("", response.discount!!.amoDiscount)
        assertEquals("", response.discount!!.amoNew)
    }

    @Test
    fun `parseTransactionResponse success - customer data parsed`() {
        val xml = loadFixture("transaction-success.xml")
        val result = RadixXmlParser.parseTransactionResponse(xml)
        val response = (result as RadixParseResult.Success).value

        assertNotNull("Customer data must be parsed", response.customerData)
        assertEquals(0, response.customerData!!.used)
    }

    @Test
    fun `parseTransactionResponse success - signature extracted`() {
        val xml = loadFixture("transaction-success.xml")
        val result = RadixXmlParser.parseTransactionResponse(xml)
        val response = (result as RadixParseResult.Success).value

        assertEquals("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2", response.signature)
    }

    // -----------------------------------------------------------------------
    // Transaction response — Empty / No Transaction (RESP_CODE=205)
    // -----------------------------------------------------------------------

    @Test
    fun `parseTransactionResponse empty - transaction is null and respCode is 205`() {
        val xml = loadFixture("transaction-empty.xml")
        val result = RadixXmlParser.parseTransactionResponse(xml)

        assertTrue("Parse must succeed", result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value

        assertEquals(205, response.respCode)
        assertEquals("NO TRN AVAILABLE", response.respMsg)
        assertEquals("12345", response.token)
        assertNull("Transaction must be null for RESP_CODE=205", response.transaction)
    }

    @Test
    fun `parseTransactionResponse empty - RFID and discount null for empty self-closing elements`() {
        val xml = loadFixture("transaction-empty.xml")
        val result = RadixXmlParser.parseTransactionResponse(xml)
        val response = (result as RadixParseResult.Success).value

        assertNull("RFID card must be null for empty <RFID_CARD />", response.rfidCard)
        assertNull("Discount must be null for empty <DISCOUNT />", response.discount)
    }

    // -----------------------------------------------------------------------
    // Transaction response — Unsolicited (RESP_CODE=30)
    // -----------------------------------------------------------------------

    @Test
    fun `parseTransactionResponse unsolicited - respCode is 30 with TRN data present`() {
        val xml = loadFixture("transaction-unsolicited.xml")
        val result = RadixXmlParser.parseTransactionResponse(xml)

        assertTrue("Parse must succeed", result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value

        assertEquals(30, response.respCode)
        assertEquals("UNSOL_TRN", response.respMsg)
        assertEquals("54321", response.token)

        assertNotNull("Transaction must be present for RESP_CODE=30", response.transaction)
        val trn = response.transaction!!

        assertEquals("15000.0", trn.amo)
        assertEquals("DIESEL", trn.fdcProdName)
        assertEquals("8.11", trn.vol)
        assertEquals("1", trn.fp)
        assertEquals("368990", trn.fdcSaveNum)
    }

    // -----------------------------------------------------------------------
    // Transaction response — Error codes
    // -----------------------------------------------------------------------

    @Test
    fun `parseTransactionResponse signature error - respCode 251 with null transaction`() {
        val xml = loadFixture("transaction-signature-error.xml")
        val result = RadixXmlParser.parseTransactionResponse(xml)

        assertTrue("Parse must succeed", result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value

        assertEquals(251, response.respCode)
        assertEquals("SIGNATURE ERROR", response.respMsg)
        assertNull("Transaction must be null for error RESP_CODE=251", response.transaction)
    }

    @Test
    fun `parseTransactionResponse handles all transaction error codes`() {
        val errorCodes = listOf(206, 253, 255)
        for (code in errorCodes) {
            val xml = """<?xml version="1.0" encoding="UTF-8"?>
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="$code" RESP_MSG="ERROR" TOKEN="1" />
    <TRN />
  </TABLE>
  <SIGNATURE>0000000000000000000000000000000000000000</SIGNATURE>
</FDC_RESP>"""
            val result = RadixXmlParser.parseTransactionResponse(xml)
            assertTrue("RESP_CODE=$code must parse successfully", result is RadixParseResult.Success)
            val response = (result as RadixParseResult.Success).value
            assertEquals("RESP_CODE must be $code", code, response.respCode)
            assertNull("Transaction must be null for error code $code", response.transaction)
        }
    }

    // -----------------------------------------------------------------------
    // Auth response — Success (ACKCODE=0)
    // -----------------------------------------------------------------------

    @Test
    fun `parseAuthResponse success - ackCode is 0 and ackMsg is Success`() {
        val xml = loadFixture("auth-success.xml")
        val result = RadixXmlParser.parseAuthResponse(xml)

        assertTrue("Parse must succeed", result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value

        assertEquals(0, response.ackCode)
        assertEquals("Success", response.ackMsg)
        assertEquals("2021-03-01", response.date)
        assertEquals("09:38:42", response.time)
        assertEquals("e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6", response.signature)
    }

    // -----------------------------------------------------------------------
    // Auth response — Error codes
    // -----------------------------------------------------------------------

    @Test
    fun `parseAuthResponse pump not ready - ackCode is 258`() {
        val xml = loadFixture("auth-pump-not-ready.xml")
        val result = RadixXmlParser.parseAuthResponse(xml)

        assertTrue("Parse must succeed", result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value

        assertEquals(258, response.ackCode)
        assertEquals("Pump not ready", response.ackMsg)
    }

    @Test
    fun `parseAuthResponse DSB offline - ackCode is 260`() {
        val xml = loadFixture("auth-dsb-offline.xml")
        val result = RadixXmlParser.parseAuthResponse(xml)

        assertTrue("Parse must succeed", result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value

        assertEquals(260, response.ackCode)
        assertEquals("DSB is offline", response.ackMsg)
    }

    @Test
    fun `parseAuthResponse signature error - ackCode is 251`() {
        val xml = loadFixture("auth-signature-error.xml")
        val result = RadixXmlParser.parseAuthResponse(xml)

        assertTrue("Parse must succeed", result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value

        assertEquals(251, response.ackCode)
        assertEquals("Signature error", response.ackMsg)
    }

    @Test
    fun `parseAuthResponse handles all auth error codes`() {
        val errorCases = listOf(
            255 to "Bad XML format",
            256 to "Bad header format",
        )
        for ((code, msg) in errorCases) {
            val xml = """<?xml version="1.0" encoding="UTF-8"?>
<FDCMS>
  <FDCACK>
    <DATE>2021-03-01</DATE>
    <TIME>10:00:00</TIME>
    <ACKCODE>$code</ACKCODE>
    <ACKMSG>$msg</ACKMSG>
  </FDCACK>
  <FDCSIGNATURE>0000000000000000000000000000000000000000</FDCSIGNATURE>
</FDCMS>"""
            val result = RadixXmlParser.parseAuthResponse(xml)
            assertTrue("ACKCODE=$code must parse successfully", result is RadixParseResult.Success)
            val response = (result as RadixParseResult.Success).value
            assertEquals("ACKCODE must be $code", code, response.ackCode)
            assertEquals(msg, response.ackMsg)
        }
    }

    // -----------------------------------------------------------------------
    // Product response
    // -----------------------------------------------------------------------

    @Test
    fun `parseProductResponse success - all products parsed with correct attributes`() {
        val xml = loadFixture("products-success.xml")
        val result = RadixXmlParser.parseProductResponse(xml)

        assertTrue("Parse must succeed", result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value

        assertEquals(201, response.respCode)
        assertEquals("SUCCESS", response.respMsg)
        assertEquals(3, response.products.size)

        assertEquals(0, response.products[0].id)
        assertEquals("UNLEADED", response.products[0].name)
        assertEquals("1930", response.products[0].price)

        assertEquals(1, response.products[1].id)
        assertEquals("DIESEL", response.products[1].name)
        assertEquals("1850", response.products[1].price)

        assertEquals(2, response.products[2].id)
        assertEquals("SUPER", response.products[2].name)
        assertEquals("2100", response.products[2].price)
    }

    @Test
    fun `parseProductResponse with no PRODUCT elements returns empty list`() {
        val xml = """<?xml version="1.0" encoding="UTF-8"?>
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="1" />
  </TABLE>
  <SIGNATURE>0000000000000000000000000000000000000000</SIGNATURE>
</FDC_RESP>"""
        val result = RadixXmlParser.parseProductResponse(xml)

        assertTrue(result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value
        assertTrue("Products list must be empty", response.products.isEmpty())
    }

    // -----------------------------------------------------------------------
    // Missing optional attributes — graceful handling
    // -----------------------------------------------------------------------

    @Test
    fun `parseTransactionResponse with missing optional TRN attributes - no exception and fields empty`() {
        val xml = """<?xml version="1.0" encoding="UTF-8"?>
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="1" />
    <TRN AMO="100.0" FDC_NUM="999" FDC_SAVE_NUM="1" VOL="5.0" />
  </TABLE>
  <SIGNATURE>0000000000000000000000000000000000000000</SIGNATURE>
</FDC_RESP>"""
        val result = RadixXmlParser.parseTransactionResponse(xml)

        assertTrue("Parse must succeed with partial attributes", result is RadixParseResult.Success)
        val trn = (result as RadixParseResult.Success).value.transaction
        assertNotNull(trn)
        trn!!

        // Provided attributes
        assertEquals("100.0", trn.amo)
        assertEquals("999", trn.fdcNum)
        assertEquals("1", trn.fdcSaveNum)
        assertEquals("5.0", trn.vol)

        // Missing attributes default to empty string
        assertEquals("", trn.efdId)
        assertEquals("", trn.fdcDate)
        assertEquals("", trn.fdcTime)
        assertEquals("", trn.fdcName)
        assertEquals("", trn.fdcProd)
        assertEquals("", trn.fdcProdName)
        assertEquals("", trn.fdcTank)
        assertEquals("", trn.fp)
        assertEquals("", trn.noz)
        assertEquals("", trn.price)
        assertEquals("", trn.pumpAddr)
        assertEquals("", trn.rdgDate)
        assertEquals("", trn.rdgTime)
        assertEquals("", trn.rdgId)
        assertEquals("", trn.rdgIndex)
        assertEquals("", trn.rdgProd)
        assertEquals("", trn.rdgSaveNum)
        assertEquals("", trn.regId)
        assertEquals("", trn.roundType)
    }

    @Test
    fun `parseTransactionResponse with no CUST_DATA element - customerData is null`() {
        val xml = """<?xml version="1.0" encoding="UTF-8"?>
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="1" />
    <TRN AMO="100.0" />
  </TABLE>
  <SIGNATURE>0000000000000000000000000000000000000000</SIGNATURE>
</FDC_RESP>"""
        val result = RadixXmlParser.parseTransactionResponse(xml)

        assertTrue(result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value
        assertNull("Customer data must be null when element absent", response.customerData)
        assertNull("RFID card must be null when element absent", response.rfidCard)
        assertNull("Discount must be null when element absent", response.discount)
    }

    // -----------------------------------------------------------------------
    // Signature validation — Transaction responses
    // -----------------------------------------------------------------------

    @Test
    fun `validateTransactionResponseSignature returns true for correctly signed response`() {
        val tableContent = """<TABLE VERSION="1.0"><ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="12345" /><TRN AMO="30000.0" /></TABLE>"""
        // Compute correct signature: SHA1(tableContent + SECRET)
        val correctSig = RadixSignatureHelper.computeTransactionSignature(tableContent, SECRET)
        val xml = "<FDC_RESP>${tableContent}<SIGNATURE>${correctSig}</SIGNATURE></FDC_RESP>"

        assertTrue(
            "Validation must pass for correctly signed response",
            RadixXmlParser.validateTransactionResponseSignature(xml, SECRET)
        )
    }

    @Test
    fun `validateTransactionResponseSignature returns false for tampered response`() {
        val tableContent = """<TABLE VERSION="1.0"><ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="12345" /><TRN AMO="30000.0" /></TABLE>"""
        val correctSig = RadixSignatureHelper.computeTransactionSignature(tableContent, SECRET)

        // Tamper with the content — change amount
        val tamperedXml = "<FDC_RESP>${tableContent.replace("30000.0", "99999.0")}<SIGNATURE>${correctSig}</SIGNATURE></FDC_RESP>"

        assertFalse(
            "Validation must fail for tampered response",
            RadixXmlParser.validateTransactionResponseSignature(tamperedXml, SECRET)
        )
    }

    @Test
    fun `validateTransactionResponseSignature returns false for wrong secret`() {
        val tableContent = """<TABLE VERSION="1.0"><ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="1" /></TABLE>"""
        val correctSig = RadixSignatureHelper.computeTransactionSignature(tableContent, SECRET)
        val xml = "<FDC_RESP>${tableContent}<SIGNATURE>${correctSig}</SIGNATURE></FDC_RESP>"

        assertFalse(
            "Validation must fail with wrong secret",
            RadixXmlParser.validateTransactionResponseSignature(xml, "WrongSecret")
        )
    }

    @Test
    fun `validateTransactionResponseSignature returns false when SIGNATURE element missing`() {
        val xml = "<FDC_RESP><TABLE VERSION=\"1.0\"><ANS RESP_CODE=\"201\" /></TABLE></FDC_RESP>"

        assertFalse(
            "Validation must fail when SIGNATURE element is absent",
            RadixXmlParser.validateTransactionResponseSignature(xml, SECRET)
        )
    }

    // -----------------------------------------------------------------------
    // Signature validation — Auth responses
    // -----------------------------------------------------------------------

    @Test
    fun `validateAuthResponseSignature returns true for correctly signed response`() {
        val fdcAckContent = "<FDCACK><DATE>2021-03-01</DATE><TIME>09:38:42</TIME><ACKCODE>0</ACKCODE><ACKMSG>Success</ACKMSG></FDCACK>"
        val correctSig = RadixSignatureHelper.computeAuthSignature(fdcAckContent, SECRET)
        val xml = "<FDCMS>${fdcAckContent}<FDCSIGNATURE>${correctSig}</FDCSIGNATURE></FDCMS>"

        assertTrue(
            "Validation must pass for correctly signed auth response",
            RadixXmlParser.validateAuthResponseSignature(xml, SECRET)
        )
    }

    @Test
    fun `validateAuthResponseSignature returns false for tampered response`() {
        val fdcAckContent = "<FDCACK><DATE>2021-03-01</DATE><TIME>09:38:42</TIME><ACKCODE>0</ACKCODE><ACKMSG>Success</ACKMSG></FDCACK>"
        val correctSig = RadixSignatureHelper.computeAuthSignature(fdcAckContent, SECRET)

        // Tamper with ACKCODE
        val tamperedXml = "<FDCMS>${fdcAckContent.replace("<ACKCODE>0</ACKCODE>", "<ACKCODE>255</ACKCODE>")}<FDCSIGNATURE>${correctSig}</FDCSIGNATURE></FDCMS>"

        assertFalse(
            "Validation must fail for tampered auth response",
            RadixXmlParser.validateAuthResponseSignature(tamperedXml, SECRET)
        )
    }

    @Test
    fun `validateAuthResponseSignature returns false for wrong secret`() {
        val fdcAckContent = "<FDCACK><DATE>2021-03-01</DATE><ACKCODE>0</ACKCODE></FDCACK>"
        val correctSig = RadixSignatureHelper.computeAuthSignature(fdcAckContent, SECRET)
        val xml = "<FDCMS>${fdcAckContent}<FDCSIGNATURE>${correctSig}</FDCSIGNATURE></FDCMS>"

        assertFalse(
            "Validation must fail with wrong secret",
            RadixXmlParser.validateAuthResponseSignature(xml, "WrongSecret")
        )
    }

    // -----------------------------------------------------------------------
    // Malformed XML handling
    // -----------------------------------------------------------------------

    @Test
    fun `parseTransactionResponse returns error for malformed XML`() {
        val malformedXml = "<FDC_RESP><TABLE><ANS RESP_CODE=\"201\"<BROKEN"
        val result = RadixXmlParser.parseTransactionResponse(malformedXml)

        assertTrue("Malformed XML must return Error", result is RadixParseResult.Error)
        val error = (result as RadixParseResult.Error).message
        assertTrue("Error message must describe the failure", error.isNotEmpty())
    }

    @Test
    fun `parseAuthResponse returns error for malformed XML`() {
        val malformedXml = "not xml at all"
        val result = RadixXmlParser.parseAuthResponse(malformedXml)

        assertTrue("Malformed XML must return Error", result is RadixParseResult.Error)
    }

    @Test
    fun `parseProductResponse returns error for malformed XML`() {
        val malformedXml = "<unclosed"
        val result = RadixXmlParser.parseProductResponse(malformedXml)

        assertTrue("Malformed XML must return Error", result is RadixParseResult.Error)
    }

    @Test
    fun `parseTransactionResponse returns error when ANS element missing`() {
        val xml = """<?xml version="1.0" encoding="UTF-8"?>
<FDC_RESP>
  <TABLE VERSION="1.0">
    <TRN AMO="100.0" />
  </TABLE>
  <SIGNATURE>0000000000000000000000000000000000000000</SIGNATURE>
</FDC_RESP>"""
        val result = RadixXmlParser.parseTransactionResponse(xml)

        assertTrue("Missing ANS must return Error", result is RadixParseResult.Error)
        assertTrue(
            "Error should mention ANS",
            (result as RadixParseResult.Error).message.contains("ANS")
        )
    }

    @Test
    fun `parseAuthResponse returns error when FDCACK element missing`() {
        val xml = """<FDCMS><FDCSIGNATURE>abc</FDCSIGNATURE></FDCMS>"""
        val result = RadixXmlParser.parseAuthResponse(xml)

        assertTrue("Missing FDCACK must return Error", result is RadixParseResult.Error)
        assertTrue(
            "Error should mention FDCACK",
            (result as RadixParseResult.Error).message.contains("FDCACK")
        )
    }

    @Test
    fun `parseTransactionResponse returns error when RESP_CODE is non-numeric`() {
        val xml = """<?xml version="1.0" encoding="UTF-8"?>
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="abc" RESP_MSG="BAD" TOKEN="1" />
  </TABLE>
  <SIGNATURE>0000000000000000000000000000000000000000</SIGNATURE>
</FDC_RESP>"""
        val result = RadixXmlParser.parseTransactionResponse(xml)

        assertTrue("Non-numeric RESP_CODE must return Error", result is RadixParseResult.Error)
    }

    @Test
    fun `parseAuthResponse returns error when ACKCODE is non-numeric`() {
        val xml = """<FDCMS><FDCACK><ACKCODE>xyz</ACKCODE><ACKMSG>Bad</ACKMSG></FDCACK><FDCSIGNATURE>abc</FDCSIGNATURE></FDCMS>"""
        val result = RadixXmlParser.parseAuthResponse(xml)

        assertTrue("Non-numeric ACKCODE must return Error", result is RadixParseResult.Error)
    }

    // -----------------------------------------------------------------------
    // Empty string and edge cases
    // -----------------------------------------------------------------------

    @Test
    fun `parseTransactionResponse handles empty SIGNATURE element`() {
        val xml = """<?xml version="1.0" encoding="UTF-8"?>
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="205" RESP_MSG="NO TRN AVAILABLE" TOKEN="1" />
    <TRN />
  </TABLE>
  <SIGNATURE></SIGNATURE>
</FDC_RESP>"""
        val result = RadixXmlParser.parseTransactionResponse(xml)

        assertTrue(result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value
        assertEquals("", response.signature)
    }

    @Test
    fun `parseTransactionResponse handles missing SIGNATURE element`() {
        val xml = """<?xml version="1.0" encoding="UTF-8"?>
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="205" RESP_MSG="NO TRN" TOKEN="1" />
    <TRN />
  </TABLE>
</FDC_RESP>"""
        val result = RadixXmlParser.parseTransactionResponse(xml)

        assertTrue(result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value
        assertEquals("", response.signature)
    }

    @Test
    fun `parseAuthResponse handles missing FDCSIGNATURE element`() {
        val xml = """<FDCMS><FDCACK><DATE>2021-01-01</DATE><TIME>00:00:00</TIME><ACKCODE>0</ACKCODE><ACKMSG>OK</ACKMSG></FDCACK></FDCMS>"""
        val result = RadixXmlParser.parseAuthResponse(xml)

        assertTrue(result is RadixParseResult.Success)
        val response = (result as RadixParseResult.Success).value
        assertEquals("", response.signature)
    }
}

package com.fccmiddleware.edge.adapter.radix

import android.util.Log
import io.mockk.every
import io.mockk.mockkStatic
import io.mockk.unmockkStatic
import io.mockk.verify
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * Unit tests for [RadixSignatureHelper].
 *
 * Covers:
 *   - SHA-1 computation with known input/output pairs
 *   - Transaction management signing: SHA1(<REQ>...</REQ> + password)
 *   - Pre-auth signing: SHA1(<AUTH_DATA>...</AUTH_DATA> + password)
 *   - Signature validation (match and mismatch)
 *   - Whitespace sensitivity
 *   - Empty content edge case
 *   - Special characters (Turkish, Arabic)
 *   - Secret appended immediately after closing tag with no separator
 *
 * Test framework: JUnit 5 (AndroidJUnit4 runner compatible)
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class RadixSignatureHelperTests {

    companion object {
        private const val SECRET = "MySecretPassword"
    }

    @Before
    fun setUp() {
        mockkStatic(Log::class)
        every { Log.d(any(), any()) } returns 0
        every { Log.i(any(), any()) } returns 0
        every { Log.w(any(), any<String>()) } returns 0
        every { Log.e(any(), any<String>()) } returns 0
        every { Log.e(any(), any<String>(), any()) } returns 0
        RadixSignatureHelper.resetWarningForTests()
    }

    @After
    fun tearDown() {
        RadixSignatureHelper.resetWarningForTests()
        unmockkStatic(Log::class)
    }

    // -----------------------------------------------------------------------
    // Transaction signing (port P+1): SHA1(<REQ>...</REQ> + SECRET)
    // -----------------------------------------------------------------------

    @Test
    fun `computeTransactionSignature produces correct SHA-1 for basic REQ element`() {
        val req = "<REQ><CMD_CODE>10</CMD_CODE><CMD_NAME>TRN_REQ</CMD_NAME><TOKEN>12345</TOKEN></REQ>"
        val result = RadixSignatureHelper.computeTransactionSignature(req, SECRET)
        assertEquals("d1d97346980b45e69560a799a86a9707cb56b01f", result)
    }

    @Test
    fun `computeTransactionSignature produces lowercase hex string of 40 characters`() {
        val req = "<REQ><CMD_CODE>10</CMD_CODE></REQ>"
        val result = RadixSignatureHelper.computeTransactionSignature(req, SECRET)
        assertEquals(40, result.length)
        assertTrue("Signature must be lowercase hex", result.matches(Regex("[0-9a-f]{40}")))
    }

    @Test
    fun `computeTransactionSignature for ACK request`() {
        val req = "<REQ><CMD_CODE>201</CMD_CODE></REQ>"
        val result = RadixSignatureHelper.computeTransactionSignature(req, SECRET)
        assertEquals("8802249d78f9e0eb6e600079efa8284d611027fa", result)
    }

    // -----------------------------------------------------------------------
    // Auth signing (port P): SHA1(<AUTH_DATA>...</AUTH_DATA> + SECRET)
    // -----------------------------------------------------------------------

    @Test
    fun `computeAuthSignature produces correct SHA-1 for basic AUTH_DATA element`() {
        val authData = "<AUTH_DATA><PUMP>3</PUMP><FP>1</FP><AUTH>TRUE</AUTH><TOKEN>123456</TOKEN></AUTH_DATA>"
        val result = RadixSignatureHelper.computeAuthSignature(authData, SECRET)
        assertEquals("d6302e39898c55fe0c2f15c398d27beab0a9f7bf", result)
    }

    @Test
    fun `computeAuthSignature produces lowercase hex string of 40 characters`() {
        val authData = "<AUTH_DATA><PUMP>1</PUMP></AUTH_DATA>"
        val result = RadixSignatureHelper.computeAuthSignature(authData, SECRET)
        assertEquals(40, result.length)
        assertTrue("Signature must be lowercase hex", result.matches(Regex("[0-9a-f]{40}")))
    }

    // -----------------------------------------------------------------------
    // Both signing paths produce different results for different content
    // -----------------------------------------------------------------------

    @Test
    fun `transaction and auth signatures differ for different content structures`() {
        val reqContent = "<REQ><CMD_CODE>10</CMD_CODE></REQ>"
        val authContent = "<AUTH_DATA><PUMP>1</PUMP></AUTH_DATA>"

        val txSig = RadixSignatureHelper.computeTransactionSignature(reqContent, SECRET)
        val authSig = RadixSignatureHelper.computeAuthSignature(authContent, SECRET)

        assertNotEquals("Different XML content must produce different signatures", txSig, authSig)
    }

    // -----------------------------------------------------------------------
    // Signature validation
    // -----------------------------------------------------------------------

    @Test
    fun `validateSignature returns true for matching signature`() {
        val table = """<TABLE VERSION="1.0"><ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="12345" /><TRN AMO="30000.0" /></TABLE>"""
        val expectedSig = "8080da0a07ddcd78cf5aeec5bf9595e537046142"

        assertTrue(RadixSignatureHelper.validateSignature(table, expectedSig, SECRET))
    }

    @Test
    fun `validateSignature returns false for mismatched signature`() {
        val table = """<TABLE VERSION="1.0"><ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="12345" /><TRN AMO="30000.0" /></TABLE>"""
        val wrongSig = "0000000000000000000000000000000000000000"

        assertFalse(RadixSignatureHelper.validateSignature(table, wrongSig, SECRET))
    }

    @Test
    fun `validateSignature returns false when wrong secret is used`() {
        val table = """<TABLE VERSION="1.0"><ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="12345" /><TRN AMO="30000.0" /></TABLE>"""
        val sigWithCorrectSecret = "8080da0a07ddcd78cf5aeec5bf9595e537046142"

        assertFalse(RadixSignatureHelper.validateSignature(table, sigWithCorrectSecret, "WrongPassword"))
    }

    @Test
    fun `validateSignature is case-insensitive for hex comparison`() {
        val table = """<TABLE VERSION="1.0"><ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="12345" /><TRN AMO="30000.0" /></TABLE>"""
        val upperSig = "8080DA0A07DDCD78CF5AEEC5BF9595E537046142"

        assertTrue(
            "Validation should accept uppercase hex from FDC responses",
            RadixSignatureHelper.validateSignature(table, upperSig, SECRET)
        )
    }

    // -----------------------------------------------------------------------
    // Whitespace sensitivity
    // -----------------------------------------------------------------------

    @Test
    fun `whitespace differences in XML produce different signatures`() {
        val compact = "<REQ><CMD_CODE>10</CMD_CODE><CMD_NAME>TRN_REQ</CMD_NAME><TOKEN>12345</TOKEN></REQ>"
        val formatted = "<REQ>\n    <CMD_CODE>10</CMD_CODE>\n    <CMD_NAME>TRN_REQ</CMD_NAME>\n    <TOKEN>12345</TOKEN>\n</REQ>"

        val sigCompact = RadixSignatureHelper.computeTransactionSignature(compact, SECRET)
        val sigFormatted = RadixSignatureHelper.computeTransactionSignature(formatted, SECRET)

        assertEquals("d1d97346980b45e69560a799a86a9707cb56b01f", sigCompact)
        assertEquals("1f37f81292cd10a5143366e65d3ee1376dda0c37", sigFormatted)
        assertNotEquals("Whitespace must affect signature", sigCompact, sigFormatted)
    }

    // -----------------------------------------------------------------------
    // Secret appended immediately (no separator)
    // -----------------------------------------------------------------------

    @Test
    fun `secret is appended immediately after closing tag with no separator`() {
        val req = "<REQ><CMD_CODE>201</CMD_CODE></REQ>"

        val sigNoSpace = RadixSignatureHelper.computeTransactionSignature(req, SECRET)
        // If there were a space between </REQ> and the secret, the hash would differ
        val sigWithSpace = RadixSignatureHelper.computeTransactionSignature(req + " ", SECRET.substring(1))

        assertEquals("8802249d78f9e0eb6e600079efa8284d611027fa", sigNoSpace)
        assertNotEquals(
            "Adding a space before the secret must produce a different signature",
            sigNoSpace, sigWithSpace
        )
    }

    // -----------------------------------------------------------------------
    // Empty content
    // -----------------------------------------------------------------------

    @Test
    fun `empty content with secret produces valid SHA-1`() {
        val result = RadixSignatureHelper.computeTransactionSignature("", SECRET)
        assertEquals("952729c61cab7e01e4b5f5ba7b95830d2075f74b", result)
        assertEquals(40, result.length)
    }

    // -----------------------------------------------------------------------
    // Special characters (Unicode)
    // -----------------------------------------------------------------------

    @Test
    fun `special characters - Turkish in XML content`() {
        val req = "<REQ><CMD_NAME>T\u00fcrk\u00e7e \u0130\u015flem</CMD_NAME></REQ>"
        val result = RadixSignatureHelper.computeTransactionSignature(req, SECRET)
        assertEquals("5ba8c2bc9a0dc9eb660ae99ff5e17bf8adf9d47c", result)
    }

    @Test
    fun `special characters - Arabic in XML content`() {
        val req = "<REQ><CMD_NAME>\u0639\u0645\u0644\u064a\u0629</CMD_NAME></REQ>"
        val result = RadixSignatureHelper.computeTransactionSignature(req, SECRET)
        assertEquals("bc8137396e91fc2838ae9cdcca8e3caf65e9ea58", result)
    }

    // -----------------------------------------------------------------------
    // Deterministic output
    // -----------------------------------------------------------------------

    @Test
    fun `repeated calls with same input produce identical signatures`() {
        val req = "<REQ><CMD_CODE>10</CMD_CODE></REQ>"
        val sig1 = RadixSignatureHelper.computeTransactionSignature(req, SECRET)
        val sig2 = RadixSignatureHelper.computeTransactionSignature(req, SECRET)
        assertEquals(sig1, sig2)
    }

    @Test
    fun `runtime SHA-1 warning is logged only once`() {
        val req = "<REQ><CMD_CODE>10</CMD_CODE></REQ>"
        val authData = "<AUTH_DATA><PUMP>1</PUMP></AUTH_DATA>"
        val table = """<TABLE VERSION="1.0"><ANS RESP_CODE="201" /></TABLE>"""

        RadixSignatureHelper.computeTransactionSignature(req, SECRET)
        RadixSignatureHelper.computeAuthSignature(authData, SECRET)
        RadixSignatureHelper.validateSignature(table, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", SECRET)

        verify(exactly = 1) {
            Log.w(
                "RadixSignatureHelper",
                match { it.contains("SHA-1") && it.contains("vendor limitation") },
            )
        }
    }
}

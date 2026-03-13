package com.fccmiddleware.edge.adapter.radix

import android.util.Log
import com.fccmiddleware.edge.adapter.common.*
import io.ktor.client.HttpClient
import io.ktor.client.engine.mock.MockEngine
import io.ktor.client.engine.mock.respond
import io.ktor.client.request.post
import io.ktor.client.request.setBody
import io.ktor.client.statement.bodyAsText
import io.ktor.http.HttpStatusCode
import io.ktor.http.headersOf
import io.mockk.*
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.IOException
import java.net.SocketTimeoutException

// ---------------------------------------------------------------------------
// Unit tests for RadixAdapter.
//
// Covers:
//   - Heartbeat: successful (RESP_CODE=201), unreachable, timeout,
//     signature error (RESP_CODE=251), bad XML
//   - Heartbeat request targets port P+1 with Operation=2 header
//   - acknowledgeTransactions() always returns true (no-op)
//   - getPumpStatus() always returns empty list
//
// Test framework: JUnit + MockK + Ktor MockEngine
// ---------------------------------------------------------------------------
class RadixAdapterTests {

    @Before
    fun setUp() {
        mockkStatic(Log::class)
        every { Log.d(any(), any()) } returns 0
        every { Log.w(any(), any<String>()) } returns 0
    }

    @After
    fun tearDown() {
        unmockkStatic(Log::class)
    }

    private fun createConfig(
        host: String = "192.168.1.100",
        authPort: Int = 5002,
        sharedSecret: String = "TestSecret",
        usnCode: Int = 100,
    ): AgentFccConfig = AgentFccConfig(
        fccVendor = FccVendor.RADIX,
        connectionProtocol = "HTTP_XML",
        hostAddress = host,
        port = authPort,
        authCredential = "",
        ingestionMode = IngestionMode.BUFFER_ALWAYS,
        pullIntervalSeconds = 30,
        productCodeMapping = emptyMap(),
        timezone = "Africa/Dar_es_Salaam",
        currencyCode = "TZS",
        sharedSecret = sharedSecret,
        usnCode = usnCode,
        authPort = authPort,
    )

    private fun productResponseXml(respCode: Int, respMsg: String, products: String = ""): String = """
        <?xml version="1.0" encoding="UTF-8"?>
        <FDC_RESP>
            <TABLE VERSION="1.0">
                <ANS RESP_CODE="$respCode" RESP_MSG="$respMsg" />
                $products
            </TABLE>
            <SIGNATURE>abc123</SIGNATURE>
        </FDC_RESP>
    """.trimIndent()

    private fun authResponseXml(ackCode: Int, ackMsg: String, sharedSecret: String = "TestSecret"): String {
        val ackContent = """
            <FDCACK>
                <DATE>2026-03-13</DATE>
                <TIME>10:00:00</TIME>
                <ACKCODE>$ackCode</ACKCODE>
                <ACKMSG>$ackMsg</ACKMSG>
            </FDCACK>
        """.trimIndent()
        val signature = RadixSignatureHelper.computeAuthSignature(ackContent, sharedSecret)
        return """
            <?xml version="1.0" encoding="UTF-8"?>
            <FDCMS>
                $ackContent
                <FDCSIGNATURE>$signature</FDCSIGNATURE>
            </FDCMS>
        """.trimIndent()
    }

    private fun createPreAuthCommand() = PreAuthCommand(
        siteCode = "SITE-A",
        pumpNumber = 1,
        amountMinorUnits = 5_000L,
        unitPrice = 100L,
        currencyCode = "TZS",
        odooOrderId = "ORD-001",
    )

    private fun createCancelCommand() = CancelPreAuthCommand(
        siteCode = "SITE-A",
        pumpNumber = 1,
        fccCorrelationId = "RADIX-TOKEN-1",
    )

    // -----------------------------------------------------------------------
    // Heartbeat — successful (RESP_CODE=201 → true)
    // -----------------------------------------------------------------------

    @Test
    fun `ktor mock engine returns body correctly`() = runTest {
        val xml = productResponseXml(201, "SUCCESS")
        val mockEngine = MockEngine { respond(
            content = xml,
            status = HttpStatusCode.OK,
            headers = headersOf("Content-Type" to listOf("Application/xml")),
        ) }
        val client = HttpClient(mockEngine)
        val response = client.post("http://localhost:9999") {
            setBody("test")
        }
        val body = response.bodyAsText()
        assertEquals(xml, body)
    }

    @Test
    fun `heartbeat returns true when RESP_CODE is 201`() = runTest(UnconfinedTestDispatcher()) {
        val xml = productResponseXml(201, "SUCCESS", """<PRODUCT ID="0" NAME="UNLEADED" PRICE="1930" />""")
        val mockEngine = MockEngine { respond(
            content = xml,
            status = HttpStatusCode.OK,
            headers = headersOf("Content-Type" to listOf("Application/xml")),
        ) }
        val adapter = RadixAdapter(createConfig(), HttpClient(mockEngine))

        assertTrue(adapter.heartbeat())
    }

    // -----------------------------------------------------------------------
    // Heartbeat — FCC unreachable (IOException → false)
    // -----------------------------------------------------------------------

    @Test
    fun `heartbeat returns false when FCC is unreachable`() = runTest {
        val mockEngine = MockEngine { throw IOException("Connection refused") }
        val adapter = RadixAdapter(createConfig(), HttpClient(mockEngine))

        assertFalse(adapter.heartbeat())
    }

    // -----------------------------------------------------------------------
    // Heartbeat — timeout (SocketTimeoutException → false)
    // -----------------------------------------------------------------------

    @Test
    fun `heartbeat returns false on socket timeout`() = runTest {
        val mockEngine = MockEngine { throw SocketTimeoutException("Read timed out") }
        val adapter = RadixAdapter(createConfig(), HttpClient(mockEngine))

        assertFalse(adapter.heartbeat())
    }

    // -----------------------------------------------------------------------
    // Heartbeat — signature error (RESP_CODE=251 → false, logged as warning)
    // -----------------------------------------------------------------------

    @Test
    fun `heartbeat returns false on signature error and logs warning`() = runTest {
        val mockEngine = MockEngine { respond(
            content = productResponseXml(251, "SIGNATURE_ERR"),
            status = HttpStatusCode.OK,
            headers = headersOf("Content-Type" to listOf("Application/xml")),
        ) }
        val adapter = RadixAdapter(createConfig(), HttpClient(mockEngine))

        assertFalse(adapter.heartbeat())
        verify { Log.w("RadixAdapter", match<String> { it.contains("signature error") && it.contains("251") }) }
    }

    // -----------------------------------------------------------------------
    // Heartbeat — bad XML response → false
    // -----------------------------------------------------------------------

    @Test
    fun `heartbeat returns false on bad XML response`() = runTest {
        val mockEngine = MockEngine { respond(
            content = "this is not xml at all",
            status = HttpStatusCode.OK,
            headers = headersOf("Content-Type" to listOf("Application/xml")),
        ) }
        val adapter = RadixAdapter(createConfig(), HttpClient(mockEngine))

        assertFalse(adapter.heartbeat())
    }

    // -----------------------------------------------------------------------
    // Heartbeat — correct URL (port P+1) and headers (Operation=2)
    // -----------------------------------------------------------------------

    @Test
    fun `heartbeat posts to port P+1 with Operation 2 and USN-Code headers`() = runTest {
        val mockEngine = MockEngine { request ->
            assertEquals("10.0.0.1", request.url.host)
            assertEquals(5003, request.url.port)
            assertEquals("2", request.headers["Operation"])
            assertEquals("100", request.headers["USN-Code"])
            respond(
                content = productResponseXml(201, "SUCCESS"),
                status = HttpStatusCode.OK,
                headers = headersOf("Content-Type" to listOf("Application/xml")),
            )
        }
        val adapter = RadixAdapter(
            createConfig(host = "10.0.0.1", authPort = 5002, usnCode = 100),
            HttpClient(mockEngine),
        )

        assertTrue(adapter.heartbeat())
    }

    // -----------------------------------------------------------------------
    // Heartbeat — non-201 response code returns false
    // -----------------------------------------------------------------------

    @Test
    fun `heartbeat returns false on non-201 non-251 response code`() = runTest {
        val mockEngine = MockEngine { respond(
            content = productResponseXml(205, "NO_DATA"),
            status = HttpStatusCode.OK,
            headers = headersOf("Content-Type" to listOf("Application/xml")),
        ) }
        val adapter = RadixAdapter(createConfig(), HttpClient(mockEngine))

        assertFalse(adapter.heartbeat())
    }

    // -----------------------------------------------------------------------
    // Existing contract tests
    // -----------------------------------------------------------------------

    @Test
    fun `acknowledgeTransactions always returns true`() = runTest {
        val adapter = RadixAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        assertTrue(adapter.acknowledgeTransactions(listOf("txn-1", "txn-2")))
    }

    @Test
    fun `getPumpStatus always returns empty list`() = runTest {
        val adapter = RadixAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        assertTrue(adapter.getPumpStatus().isEmpty())
    }

    @Test
    fun `sendPreAuth returns error when auth response signature is invalid`() = runTest {
        val tamperedXml = authResponseXml(0, "AUTHORIZED").replace("AUTHORIZED", "FORGED")
        val mockEngine = MockEngine {
            respond(
                content = tamperedXml,
                status = HttpStatusCode.OK,
                headers = headersOf("Content-Type" to listOf("Application/xml")),
            )
        }
        val adapter = RadixAdapter(
            createConfig().copy(fccPumpAddressMap = """{"1":{"pumpAddr":0,"fp":0}}"""),
            HttpClient(mockEngine),
        )

        val result = adapter.sendPreAuth(createPreAuthCommand())

        assertEquals(PreAuthResultStatus.ERROR, result.status)
        assertEquals("Auth response signature mismatch", result.message)
    }

    @Test
    fun `cancelPreAuth returns false when auth response signature is invalid`() = runTest {
        val tamperedXml = authResponseXml(0, "CANCELLED").replace("CANCELLED", "FORGED")
        val mockEngine = MockEngine {
            respond(
                content = tamperedXml,
                status = HttpStatusCode.OK,
                headers = headersOf("Content-Type" to listOf("Application/xml")),
            )
        }
        val adapter = RadixAdapter(
            createConfig().copy(fccPumpAddressMap = """{"1":{"pumpAddr":0,"fp":0}}"""),
            HttpClient(mockEngine),
        )

        val result = adapter.cancelPreAuth(createCancelCommand())

        assertFalse(result)
    }
}

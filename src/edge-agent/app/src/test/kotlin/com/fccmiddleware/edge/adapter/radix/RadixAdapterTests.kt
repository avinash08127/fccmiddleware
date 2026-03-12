package com.fccmiddleware.edge.adapter.radix

import android.util.Log
import com.fccmiddleware.edge.adapter.common.*
import io.ktor.client.HttpClient
import io.ktor.client.engine.mock.MockEngine
import io.ktor.client.engine.mock.respond
import io.ktor.http.HttpStatusCode
import io.ktor.http.headersOf
import io.mockk.*
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

    // -----------------------------------------------------------------------
    // Heartbeat — successful (RESP_CODE=201 → true)
    // -----------------------------------------------------------------------

    @Test
    fun `heartbeat returns true when RESP_CODE is 201`() = runTest {
        val xml = productResponseXml(201, "SUCCESS", """<PRODUCT ID="0" NAME="UNLEADED" PRICE="1930" />""")

        // Verify XML parsing works independently
        val parseResult = RadixXmlParser.parseProductResponse(xml)
        assertTrue("Parse should succeed", parseResult is RadixParseResult.Success)
        assertEquals(201, (parseResult as RadixParseResult.Success).value.respCode)

        val mockEngine = MockEngine { respond(
            content = xml,
            status = HttpStatusCode.OK,
            headers = headersOf("Content-Type" to listOf("Application/xml")),
        ) }
        val adapter = RadixAdapter(createConfig(), HttpClient(mockEngine))

        val result = adapter.heartbeat()
        assertTrue("Heartbeat should return true for RESP_CODE=201 but got false", result)
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
}

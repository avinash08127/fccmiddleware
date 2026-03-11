package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import io.ktor.client.request.get
import io.ktor.client.request.post
import io.ktor.client.request.setBody
import io.ktor.client.statement.bodyAsText
import io.ktor.http.ContentType
import io.ktor.http.HttpStatusCode
import io.ktor.http.contentType
import io.ktor.serialization.kotlinx.json.json
import io.ktor.server.application.install
import io.ktor.server.plugins.contentnegotiation.ContentNegotiation
import io.ktor.server.plugins.statuspages.StatusPages
import io.ktor.server.routing.routing
import io.ktor.server.testing.testApplication
import io.mockk.coEvery
import io.mockk.mockk
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant
import java.util.UUID

/**
 * TransactionRoutesTest — Ktor test application tests for all transaction endpoints.
 *
 * Validates:
 *   - GET /api/v1/transactions returns paginated list excluding SYNCED_TO_ODOO
 *   - GET /api/v1/transactions?pumpNumber=N filters by pump
 *   - GET /api/v1/transactions?since=... validates ISO 8601 and filters
 *   - GET /api/v1/transactions?limit=N respects bounds (1–100)
 *   - GET /api/v1/transactions/{id} returns 200 for existing, 404 for missing
 *   - POST /api/v1/transactions/acknowledge returns count of found IDs
 *   - Invalid 'since' returns 400 with INVALID_PARAMETER error code
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class TransactionRoutesTest {

    private lateinit var mockDao: TransactionBufferDao

    @Before
    fun setUp() {
        mockDao = mockk()
        coEvery { mockDao.countForLocalApi() } returns 0
        coEvery { mockDao.getForLocalApi(any(), any()) } returns emptyList()
        coEvery { mockDao.getForLocalApiByPump(any(), any(), any()) } returns emptyList()
        coEvery { mockDao.getForLocalApiSince(any(), any(), any()) } returns emptyList()
        coEvery { mockDao.getForLocalApiByPumpSince(any(), any(), any(), any()) } returns emptyList()
        coEvery { mockDao.getById(any()) } returns null
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/transactions
    // -------------------------------------------------------------------------

    @Test
    fun `GET transactions returns 200 with empty list`() = testApplication {
        setupRouting()

        val response = client.get("/api/v1/transactions")

        assertEquals(HttpStatusCode.OK, response.status)
        val body = response.bodyAsText()
        assertTrue("Expected 'transactions' key", body.contains("\"transactions\""))
        assertTrue("Expected 'total' key", body.contains("\"total\""))
    }

    @Test
    fun `GET transactions returns paginated result with defaults`() = testApplication {
        setupRouting()
        coEvery { mockDao.countForLocalApi() } returns 3
        coEvery { mockDao.getForLocalApi(50, 0) } returns listOf(
            buildTransaction("TX-001"),
            buildTransaction("TX-002"),
            buildTransaction("TX-003"),
        )

        val response = client.get("/api/v1/transactions")

        assertEquals(HttpStatusCode.OK, response.status)
        val body = response.bodyAsText()
        assertTrue(body.contains("TX-001"))
        assertTrue(body.contains("TX-002"))
        assertTrue(body.contains("TX-003"))
        assertTrue(body.contains("\"total\":3"))
        assertTrue(body.contains("\"limit\":50"))
        assertTrue(body.contains("\"offset\":0"))
    }

    @Test
    fun `GET transactions with pumpNumber filter calls pump DAO method`() = testApplication {
        setupRouting()
        coEvery { mockDao.getForLocalApiByPump(2, 50, 0) } returns listOf(buildTransaction("TX-P2"))

        val response = client.get("/api/v1/transactions?pumpNumber=2")

        assertEquals(HttpStatusCode.OK, response.status)
        assertTrue(response.bodyAsText().contains("TX-P2"))
    }

    @Test
    fun `GET transactions with valid since parameter calls since DAO method`() = testApplication {
        setupRouting()
        val since = "2024-01-15T10:00:00Z"
        coEvery { mockDao.getForLocalApiSince(since, 50, 0) } returns listOf(buildTransaction("TX-SINCE"))

        val response = client.get("/api/v1/transactions?since=$since")

        assertEquals(HttpStatusCode.OK, response.status)
        assertTrue(response.bodyAsText().contains("TX-SINCE"))
    }

    @Test
    fun `GET transactions with invalid since returns 400 INVALID_PARAMETER`() = testApplication {
        setupRouting()

        val response = client.get("/api/v1/transactions?since=not-a-date")

        assertEquals(HttpStatusCode.BadRequest, response.status)
        val body = response.bodyAsText()
        assertTrue(body.contains("INVALID_PARAMETER"))
    }

    @Test
    fun `GET transactions limit is capped at 100`() = testApplication {
        setupRouting()
        coEvery { mockDao.getForLocalApi(100, 0) } returns emptyList()

        val response = client.get("/api/v1/transactions?limit=999")

        assertEquals(HttpStatusCode.OK, response.status)
        assertTrue(response.bodyAsText().contains("\"limit\":100"))
    }

    @Test
    fun `GET transactions limit is at least 1`() = testApplication {
        setupRouting()
        coEvery { mockDao.getForLocalApi(1, 0) } returns emptyList()

        val response = client.get("/api/v1/transactions?limit=0")

        assertEquals(HttpStatusCode.OK, response.status)
        assertTrue(response.bodyAsText().contains("\"limit\":1"))
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/transactions/{id}
    // -------------------------------------------------------------------------

    @Test
    fun `GET transaction by id returns 200 when found`() = testApplication {
        setupRouting()
        val tx = buildTransaction("TX-FOUND")
        coEvery { mockDao.getById("TX-FOUND") } returns tx

        val response = client.get("/api/v1/transactions/TX-FOUND")

        assertEquals(HttpStatusCode.OK, response.status)
        assertTrue(response.bodyAsText().contains("TX-FOUND"))
    }

    @Test
    fun `GET transaction by id returns 404 when not found`() = testApplication {
        setupRouting()
        coEvery { mockDao.getById("MISSING") } returns null

        val response = client.get("/api/v1/transactions/MISSING")

        assertEquals(HttpStatusCode.NotFound, response.status)
        val body = response.bodyAsText()
        assertTrue(body.contains("NOT_FOUND"))
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/transactions/acknowledge
    // -------------------------------------------------------------------------

    @Test
    fun `POST acknowledge returns 200 with count of found IDs`() = testApplication {
        setupRouting()
        val tx1 = buildTransaction("TX-ACK-1")
        val tx2 = buildTransaction("TX-ACK-2")
        coEvery { mockDao.getById("TX-ACK-1") } returns tx1
        coEvery { mockDao.getById("TX-ACK-2") } returns tx2
        coEvery { mockDao.getById("TX-MISSING") } returns null

        val response = client.post("/api/v1/transactions/acknowledge") {
            contentType(ContentType.Application.Json)
            setBody("""{"transactionIds":["TX-ACK-1","TX-ACK-2","TX-MISSING"]}""")
        }

        assertEquals(HttpStatusCode.OK, response.status)
        assertTrue(response.bodyAsText().contains("\"acknowledged\":2"))
    }

    @Test
    fun `POST acknowledge with empty list returns acknowledged 0`() = testApplication {
        setupRouting()

        val response = client.post("/api/v1/transactions/acknowledge") {
            contentType(ContentType.Application.Json)
            setBody("""{"transactionIds":[]}""")
        }

        assertEquals(HttpStatusCode.OK, response.status)
        assertTrue(response.bodyAsText().contains("\"acknowledged\":0"))
    }

    @Test
    fun `POST acknowledge with invalid body returns 400`() = testApplication {
        setupRouting()

        val response = client.post("/api/v1/transactions/acknowledge") {
            contentType(ContentType.Application.Json)
            setBody("""not json""")
        }

        assertEquals(HttpStatusCode.BadRequest, response.status)
        assertTrue(response.bodyAsText().contains("INVALID_REQUEST"))
    }

    // -------------------------------------------------------------------------
    // Helper: build test application
    // -------------------------------------------------------------------------

    private fun io.ktor.server.testing.ApplicationTestBuilder.setupRouting() {
        install(ContentNegotiation) {
            json(Json { ignoreUnknownKeys = true; isLenient = false })
        }
        install(StatusPages) {
            exception<Throwable> { call, _ ->
                call.respond(
                    io.ktor.http.HttpStatusCode.InternalServerError,
                    ErrorResponse("INTERNAL_ERROR", "error", "trace", Instant.now().toString())
                )
            }
        }
        routing {
            transactionRoutes(mockDao)
        }
    }

    private fun buildTransaction(fccId: String) = BufferedTransaction(
        id = UUID.randomUUID().toString(),
        fccTransactionId = fccId,
        siteCode = "SITE_A",
        pumpNumber = 1,
        nozzleNumber = 1,
        productCode = "ULP95",
        volumeMicrolitres = 50_000_000L,
        amountMinorUnits = 75_000L,
        unitPriceMinorPerLitre = 1_500L,
        currencyCode = "MWK",
        startedAt = "2024-01-15T10:00:00Z",
        completedAt = "2024-01-15T10:05:00Z",
        fiscalReceiptNumber = null,
        fccVendor = "DOMS",
        attendantId = null,
        status = "PENDING",
        syncStatus = "PENDING",
        ingestionSource = "RELAY",
        rawPayloadJson = null,
        correlationId = UUID.randomUUID().toString(),
        uploadAttempts = 0,
        lastUploadAttemptAt = null,
        lastUploadError = null,
        schemaVersion = 1,
        createdAt = "2024-01-15T10:00:00Z",
        updatedAt = "2024-01-15T10:00:00Z",
    )
}

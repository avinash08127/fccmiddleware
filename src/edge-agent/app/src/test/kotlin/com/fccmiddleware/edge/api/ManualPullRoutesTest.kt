package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.ingestion.PollResult
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
import io.ktor.server.response.respond
import io.ktor.server.routing.routing
import io.ktor.server.testing.testApplication
import io.mockk.coEvery
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.flow.MutableStateFlow
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

/**
 * ManualPullRoutesTest — Ktor test application tests for POST /api/v1/transactions/pull.
 *
 * Validates:
 *   - 200 with newCount/skippedCount/fetchCycles/cursorAdvanced when pull succeeds
 *   - 200 with zero counts when FCC returns no new transactions (no-op pull)
 *   - 200 with skippedCount > 0 when all fetched transactions are duplicates
 *   - 503 FCC_UNREACHABLE when ConnectivityManager reports FCC not reachable
 *   - 503 FCC_UNREACHABLE when orchestrator is null (adapter not wired)
 *   - pumpNumber in request body is forwarded to orchestrator.pollNow()
 *   - Missing/empty request body is accepted (pumpNumber defaults to null)
 *   - 200 with null orchestrator result treated as zero counts
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class ManualPullRoutesTest {

    private lateinit var mockDao: TransactionBufferDao
    private lateinit var mockOrchestrator: IngestionOrchestrator
    private lateinit var mockConnectivity: ConnectivityManager

    @Before
    fun setUp() {
        mockDao = mockk()
        mockOrchestrator = mockk()
        mockConnectivity = mockk()

        // Default: FCC is reachable (FULLY_ONLINE)
        every { mockConnectivity.state } returns MutableStateFlow(ConnectivityState.FULLY_ONLINE)

        // Default: successful empty pull
        coEvery { mockOrchestrator.pollNow(any()) } returns PollResult(
            newCount = 0,
            skippedCount = 0,
            fetchCycles = 1,
            cursorAdvanced = false,
        )
        coEvery { mockOrchestrator.pollNow(null) } returns PollResult(
            newCount = 0,
            skippedCount = 0,
            fetchCycles = 1,
            cursorAdvanced = false,
        )
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/transactions/pull — happy path
    // -------------------------------------------------------------------------

    @Test
    fun `POST pull returns 200 with newCount for new transactions`() = testApplication {
        setupRouting()
        coEvery { mockOrchestrator.pollNow(null) } returns PollResult(
            newCount = 3,
            skippedCount = 0,
            fetchCycles = 1,
            cursorAdvanced = true,
        )

        val response = client.post("/api/v1/transactions/pull") {
            contentType(ContentType.Application.Json)
            setBody("{}")
        }

        assertEquals(HttpStatusCode.OK, response.status)
        val body = response.bodyAsText()
        assertTrue(body.contains("\"newCount\":3"))
        assertTrue(body.contains("\"skippedCount\":0"))
        assertTrue(body.contains("\"fetchCycles\":1"))
        assertTrue(body.contains("\"cursorAdvanced\":true"))
        assertTrue(body.contains("\"triggeredAtUtc\""))
    }

    @Test
    fun `POST pull returns 200 with zero counts when no new transactions (no-op pull)`() =
        testApplication {
            setupRouting()
            coEvery { mockOrchestrator.pollNow(null) } returns PollResult(0, 0, 1, false)

            val response = client.post("/api/v1/transactions/pull") {
                contentType(ContentType.Application.Json)
                setBody("{}")
            }

            assertEquals(HttpStatusCode.OK, response.status)
            val body = response.bodyAsText()
            assertTrue(body.contains("\"newCount\":0"))
            assertTrue(body.contains("\"skippedCount\":0"))
            assertFalse(body.contains("\"cursorAdvanced\":true"))
        }

    @Test
    fun `POST pull returns skippedCount when all fetched transactions are duplicates`() =
        testApplication {
            setupRouting()
            coEvery { mockOrchestrator.pollNow(null) } returns PollResult(
                newCount = 0,
                skippedCount = 5,
                fetchCycles = 1,
                cursorAdvanced = true,
            )

            val response = client.post("/api/v1/transactions/pull") {
                contentType(ContentType.Application.Json)
                setBody("{}")
            }

            assertEquals(HttpStatusCode.OK, response.status)
            val body = response.bodyAsText()
            assertTrue(body.contains("\"newCount\":0"))
            assertTrue(body.contains("\"skippedCount\":5"))
        }

    @Test
    fun `POST pull accepts missing request body`() = testApplication {
        setupRouting()

        val response = client.post("/api/v1/transactions/pull")

        // Missing body defaults to ManualPullRequest() with pumpNumber = null
        assertEquals(HttpStatusCode.OK, response.status)
    }

    @Test
    fun `POST pull accepts pumpNumber in request body`() = testApplication {
        setupRouting()
        coEvery { mockOrchestrator.pollNow(3) } returns PollResult(1, 0, 1, true)

        val response = client.post("/api/v1/transactions/pull") {
            contentType(ContentType.Application.Json)
            setBody("""{"pumpNumber":3}""")
        }

        assertEquals(HttpStatusCode.OK, response.status)
        assertTrue(response.bodyAsText().contains("\"newCount\":1"))
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/transactions/pull — FCC unreachable
    // -------------------------------------------------------------------------

    @Test
    fun `POST pull returns 503 when FCC is FCC_UNREACHABLE`() = testApplication {
        every { mockConnectivity.state } returns MutableStateFlow(ConnectivityState.FCC_UNREACHABLE)
        setupRouting()

        val response = client.post("/api/v1/transactions/pull") {
            contentType(ContentType.Application.Json)
            setBody("{}")
        }

        assertEquals(HttpStatusCode.ServiceUnavailable, response.status)
        val body = response.bodyAsText()
        assertTrue(body.contains("FCC_UNREACHABLE"))
    }

    @Test
    fun `POST pull returns 503 when FCC is FULLY_OFFLINE`() = testApplication {
        every { mockConnectivity.state } returns MutableStateFlow(ConnectivityState.FULLY_OFFLINE)
        setupRouting()

        val response = client.post("/api/v1/transactions/pull") {
            contentType(ContentType.Application.Json)
            setBody("{}")
        }

        assertEquals(HttpStatusCode.ServiceUnavailable, response.status)
        assertTrue(response.bodyAsText().contains("FCC_UNREACHABLE"))
    }

    @Test
    fun `POST pull returns 200 when connectivity is INTERNET_DOWN (FCC still reachable)`() =
        testApplication {
            // INTERNET_DOWN means cloud is down but FCC LAN is up — pull is valid
            every { mockConnectivity.state } returns MutableStateFlow(ConnectivityState.INTERNET_DOWN)
            setupRouting()
            coEvery { mockOrchestrator.pollNow(null) } returns PollResult(0, 0, 1, false)

            val response = client.post("/api/v1/transactions/pull") {
                contentType(ContentType.Application.Json)
                setBody("{}")
            }

            assertEquals(HttpStatusCode.OK, response.status)
        }

    // -------------------------------------------------------------------------
    // POST /api/v1/transactions/pull — orchestrator not wired
    // -------------------------------------------------------------------------

    @Test
    fun `POST pull returns 503 when orchestrator is null (adapter not wired)`() = testApplication {
        application {
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
                transactionRoutes(
                    dao = mockDao,
                    ingestionOrchestrator = null,
                    connectivityManager = mockConnectivity,
                )
            }
        }

        val response = client.post("/api/v1/transactions/pull") {
            contentType(ContentType.Application.Json)
            setBody("{}")
        }

        assertEquals(HttpStatusCode.ServiceUnavailable, response.status)
        assertTrue(response.bodyAsText().contains("FCC_UNREACHABLE"))
    }

    // -------------------------------------------------------------------------
    // Helper: build test application with all dependencies wired
    // -------------------------------------------------------------------------

    private fun io.ktor.server.testing.ApplicationTestBuilder.setupRouting() {
        application {
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
                transactionRoutes(
                    dao = mockDao,
                    ingestionOrchestrator = mockOrchestrator,
                    connectivityManager = mockConnectivity,
                )
            }
        }
    }
}

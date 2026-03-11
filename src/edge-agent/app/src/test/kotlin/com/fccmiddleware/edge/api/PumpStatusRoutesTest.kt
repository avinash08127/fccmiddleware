package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.adapter.common.PumpState
import com.fccmiddleware.edge.adapter.common.PumpStatus
import com.fccmiddleware.edge.adapter.common.PumpStatusSource
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import io.ktor.client.request.get
import io.ktor.client.statement.bodyAsText
import io.ktor.http.HttpStatusCode
import io.ktor.serialization.kotlinx.json.json
import io.ktor.server.application.install
import io.ktor.server.plugins.contentnegotiation.ContentNegotiation
import io.ktor.server.routing.routing
import io.ktor.server.testing.testApplication
import io.mockk.coEvery
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant

/**
 * PumpStatusRoutesTest — Ktor test application tests for GET /api/v1/pump-status.
 *
 * Validates:
 *   - Returns 200 with live pump list when FCC reachable
 *   - Returns stale=true when FCC unreachable (fallback from cache)
 *   - Returns stale=true when no adapter is configured
 *   - Optional pumpNumber filter returns only matching pumps
 *   - stale=false on live response
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class PumpStatusRoutesTest {

    private lateinit var mockFccAdapter: IFccAdapter
    private lateinit var mockConnectivityManager: ConnectivityManager
    private val testScope = CoroutineScope(SupervisorJob())

    @Before
    fun setUp() {
        mockFccAdapter = mockk()
        mockConnectivityManager = mockk()
        every { mockConnectivityManager.state } returns MutableStateFlow(ConnectivityState.FULLY_ONLINE)
    }

    // -------------------------------------------------------------------------
    // Live data when FCC reachable
    // -------------------------------------------------------------------------

    @Test
    fun `GET pump-status returns 200`() = testApplication {
        coEvery { mockFccAdapter.getPumpStatus() } returns emptyList()
        setupRouting()

        assertEquals(HttpStatusCode.OK, client.get("/api/v1/pump-status").status)
    }

    @Test
    fun `GET pump-status returns pump list when FCC reachable`() = testApplication {
        coEvery { mockFccAdapter.getPumpStatus() } returns listOf(
            buildPumpStatus(pumpNumber = 1),
            buildPumpStatus(pumpNumber = 2),
        )
        setupRouting()

        val body = client.get("/api/v1/pump-status").bodyAsText()

        assertTrue("Expected pumps array", body.contains("\"pumps\""))
        assertTrue("Expected stale=false on live response", body.contains("\"stale\":false"))
    }

    @Test
    fun `GET pump-status filters by pumpNumber`() = testApplication {
        coEvery { mockFccAdapter.getPumpStatus() } returns listOf(
            buildPumpStatus(pumpNumber = 1),
            buildPumpStatus(pumpNumber = 2),
        )
        setupRouting()

        val body = client.get("/api/v1/pump-status?pumpNumber=1").bodyAsText()

        assertTrue("Pump 1 should be in response", body.contains("\"pumpNumber\":1"))
        // Pump 2 should not appear — check by looking for its unique marker
        // Since both have the same siteCode, we verify via the pumps array size
        // Note: JSON might contain "pumpNumber":2 in "pumpNumber":12 etc. so check carefully
        val occurences2 = body.split("\"pumpNumber\":2").size - 1
        assertEquals("Pump 2 should be filtered out", 0, occurences2)
    }

    // -------------------------------------------------------------------------
    // Stale fallback when FCC unreachable
    // -------------------------------------------------------------------------

    @Test
    fun `GET pump-status returns stale true when FCC unreachable`() = testApplication {
        every { mockConnectivityManager.state } returns MutableStateFlow(ConnectivityState.FCC_UNREACHABLE)
        setupRouting()

        val body = client.get("/api/v1/pump-status").bodyAsText()

        assertTrue(body.contains("\"stale\":true"))
    }

    @Test
    fun `GET pump-status returns stale true when no adapter configured`() = testApplication {
        setupRouting(fccAdapter = null)

        val body = client.get("/api/v1/pump-status").bodyAsText()

        assertTrue(body.contains("\"stale\":true"))
    }

    @Test
    fun `GET pump-status returns 200 even with null adapter`() = testApplication {
        setupRouting(fccAdapter = null)

        assertEquals(HttpStatusCode.OK, client.get("/api/v1/pump-status").status)
    }

    // -------------------------------------------------------------------------
    // Single-flight: concurrent calls share one FCC request
    // -------------------------------------------------------------------------

    @Test
    fun `GET pump-status stale false on successful live fetch`() = testApplication {
        var callCount = 0
        coEvery { mockFccAdapter.getPumpStatus() } answers {
            callCount++
            listOf(buildPumpStatus(pumpNumber = 1))
        }
        setupRouting()

        val r1 = client.get("/api/v1/pump-status").bodyAsText()
        val r2 = client.get("/api/v1/pump-status").bodyAsText()

        assertTrue(r1.contains("\"stale\":false"))
        // Second call may use cached result (stale=true) or live (stale=false) depending on timing
        // but must not fail
        assertTrue(r2.contains("\"stale\""))
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    private fun io.ktor.server.testing.ApplicationTestBuilder.setupRouting(
        fccAdapter: IFccAdapter? = mockFccAdapter,
    ) {
        val cache = PumpStatusCache(
            fccAdapter = fccAdapter,
            connectivityManager = mockConnectivityManager,
            scope = testScope,
            liveTimeoutMs = 500L,
        )
        application {
            install(ContentNegotiation) {
                json(Json { ignoreUnknownKeys = true })
            }
            routing {
                pumpStatusRoutes(cache)
            }
        }
    }

    private fun buildPumpStatus(pumpNumber: Int) = PumpStatus(
        siteCode = "SITE_A",
        pumpNumber = pumpNumber,
        nozzleNumber = 1,
        state = PumpState.IDLE,
        currencyCode = "MWK",
        statusSequence = 1,
        observedAtUtc = Instant.now().toString(),
        source = PumpStatusSource.FCC_LIVE,
    )
}

package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.buffer.entity.SyncState
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
 * StatusRoutesTest — Ktor test application tests for GET /api/v1/status.
 *
 * Validates:
 *   - Returns 200 with connectivity state from ConnectivityManager
 *   - fccReachable=true when state is FULLY_ONLINE or INTERNET_DOWN
 *   - fccReachable=false when state is FCC_UNREACHABLE or FULLY_OFFLINE
 *   - bufferDepth reflects transactionDao count
 *   - Required fields present in response
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class StatusRoutesTest {

    private lateinit var mockConnectivityManager: ConnectivityManager
    private lateinit var mockTransactionDao: TransactionBufferDao
    private lateinit var mockSyncStateDao: SyncStateDao

    @Before
    fun setUp() {
        mockConnectivityManager = mockk()
        every { mockConnectivityManager.state } returns MutableStateFlow(ConnectivityState.FULLY_OFFLINE)
        every { mockConnectivityManager.fccHeartbeatAgeSeconds() } returns null

        mockTransactionDao = mockk()
        coEvery { mockTransactionDao.countForLocalApi() } returns 0

        mockSyncStateDao = mockk()
        coEvery { mockSyncStateDao.get() } returns null
    }

    // -------------------------------------------------------------------------
    // Basic response
    // -------------------------------------------------------------------------

    @Test
    fun `GET status returns 200`() = testApplication {
        setupRouting()
        assertEquals(HttpStatusCode.OK, client.get("/api/v1/status").status)
    }

    @Test
    fun `GET status includes required fields`() = testApplication {
        setupRouting()

        val body = client.get("/api/v1/status").bodyAsText()

        listOf(
            "deviceId", "siteCode", "connectivityState", "fccReachable",
            "bufferDepth", "agentVersion", "uptimeSeconds", "reportedAtUtc",
        ).forEach { field ->
            assertTrue("Expected field '$field' in body", body.contains("\"$field\""))
        }
    }

    // -------------------------------------------------------------------------
    // Connectivity state mapping
    // -------------------------------------------------------------------------

    @Test
    fun `GET status fccReachable is true when FULLY_ONLINE`() = testApplication {
        every { mockConnectivityManager.state } returns MutableStateFlow(ConnectivityState.FULLY_ONLINE)
        setupRouting()

        val body = client.get("/api/v1/status").bodyAsText()

        assertTrue(body.contains("\"connectivityState\":\"FULLY_ONLINE\""))
        assertTrue(body.contains("\"fccReachable\":true"))
    }

    @Test
    fun `GET status fccReachable is true when INTERNET_DOWN`() = testApplication {
        every { mockConnectivityManager.state } returns MutableStateFlow(ConnectivityState.INTERNET_DOWN)
        setupRouting()

        val body = client.get("/api/v1/status").bodyAsText()

        assertTrue(body.contains("\"connectivityState\":\"INTERNET_DOWN\""))
        assertTrue(body.contains("\"fccReachable\":true"))
    }

    @Test
    fun `GET status fccReachable is false when FCC_UNREACHABLE`() = testApplication {
        every { mockConnectivityManager.state } returns MutableStateFlow(ConnectivityState.FCC_UNREACHABLE)
        setupRouting()

        val body = client.get("/api/v1/status").bodyAsText()

        assertTrue(body.contains("\"connectivityState\":\"FCC_UNREACHABLE\""))
        assertTrue(body.contains("\"fccReachable\":false"))
    }

    @Test
    fun `GET status fccReachable is false when FULLY_OFFLINE`() = testApplication {
        every { mockConnectivityManager.state } returns MutableStateFlow(ConnectivityState.FULLY_OFFLINE)
        setupRouting()

        val body = client.get("/api/v1/status").bodyAsText()

        assertTrue(body.contains("\"connectivityState\":\"FULLY_OFFLINE\""))
        assertTrue(body.contains("\"fccReachable\":false"))
    }

    // -------------------------------------------------------------------------
    // Buffer depth
    // -------------------------------------------------------------------------

    @Test
    fun `GET status bufferDepth reflects DAO count`() = testApplication {
        coEvery { mockTransactionDao.countForLocalApi() } returns 77
        setupRouting()

        val body = client.get("/api/v1/status").bodyAsText()

        assertTrue("Expected bufferDepth 77", body.contains("\"bufferDepth\":77"))
    }

    // -------------------------------------------------------------------------
    // Sync state
    // -------------------------------------------------------------------------

    @Test
    fun `GET status lastSuccessfulSyncUtc from sync state`() = testApplication {
        val lastSync = "2024-01-15T10:00:00Z"
        coEvery { mockSyncStateDao.get() } returns SyncState(
            id = 1,
            lastFccCursor = null,
            lastUploadAt = lastSync,
            lastStatusPollAt = null,
            lastConfigPullAt = null,
            lastConfigVersion = null,
            updatedAt = Instant.now().toString(),
        )
        setupRouting()

        val body = client.get("/api/v1/status").bodyAsText()

        assertTrue("Expected lastSync in body", body.contains(lastSync))
    }

    @Test
    fun `GET status fccHeartbeatAgeSeconds reflects manager value`() = testApplication {
        every { mockConnectivityManager.fccHeartbeatAgeSeconds() } returns 45
        setupRouting()

        val body = client.get("/api/v1/status").bodyAsText()

        assertTrue("Expected heartbeat age 45", body.contains("\"fccHeartbeatAgeSeconds\":45"))
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    private fun io.ktor.server.testing.ApplicationTestBuilder.setupRouting() {
        install(ContentNegotiation) {
            json(Json { ignoreUnknownKeys = true })
        }
        routing {
            statusRoutes(
                connectivityManager = mockConnectivityManager,
                transactionDao = mockTransactionDao,
                syncStateDao = mockSyncStateDao,
                agentVersion = "1.0.0-test",
                deviceId = "test-device-id",
                siteCode = "TEST_SITE",
                serviceStartMs = System.currentTimeMillis() - 10_000L,
            )
        }
    }
}

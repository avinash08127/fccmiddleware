package com.fccmiddleware.edge.benchmark

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.api.PumpStatusCache
import com.fccmiddleware.edge.api.preAuthRoutes
import com.fccmiddleware.edge.api.pumpStatusRoutes
import com.fccmiddleware.edge.api.statusRoutes
import com.fccmiddleware.edge.api.transactionRoutes
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.preauth.PreAuthHandler
import io.ktor.client.request.*
import io.ktor.client.statement.*
import io.ktor.http.*
import io.ktor.serialization.kotlinx.json.json
import io.ktor.server.application.install
import io.ktor.server.plugins.contentnegotiation.ContentNegotiation
import io.ktor.server.routing.routing
import io.ktor.server.testing.*
import io.mockk.coEvery
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.serialization.json.Json
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * LocalApiBenchmarkTest — validates Ktor local API latency guardrails.
 *
 * Pass/fail thresholds from edge-agent-performance-budgets.md §1:
 *   - GET /api/transactions: p95 ≤ 150 ms
 *   - GET /api/status: p95 ≤ 100 ms
 *
 * Uses Ktor testApplication (in-process, no network) to isolate
 * server-side overhead from network variance.
 *
 * NOTE: When routes are implemented (EA-1.x), replace 501 assertion
 * with 200 + payload validation.
 */
class LocalApiBenchmarkTest {

    private val transactionDao: TransactionBufferDao = mockk(relaxed = true)
    private val syncStateDao: SyncStateDao = mockk(relaxed = true)
    private val connectivityManager: ConnectivityManager = mockk(relaxed = true)
    private val configManager: ConfigManager = mockk(relaxed = true)
    private val preAuthHandler: PreAuthHandler = mockk(relaxed = true)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)

    private fun io.ktor.server.testing.ApplicationTestBuilder.setupRoutes() {
        every { connectivityManager.state } returns MutableStateFlow(ConnectivityState.FULLY_ONLINE)
        every { connectivityManager.fccHeartbeatAgeSeconds() } returns 1
        coEvery { transactionDao.getForLocalApi(any(), any()) } returns emptyList()
        coEvery { transactionDao.countForLocalApi() } returns 0
        coEvery { syncStateDao.get() } returns null

        application {
            install(ContentNegotiation) {
                json(Json { ignoreUnknownKeys = true })
            }
            routing {
                transactionRoutes(transactionDao)
                statusRoutes(
                    connectivityManager = connectivityManager,
                    transactionDao = transactionDao,
                    syncStateDao = syncStateDao,
                    configManager = configManager,
                    agentVersion = "1.0.0-test",
                    deviceId = "benchmark-device",
                    siteCode = "BENCH",
                    serviceStartMs = System.currentTimeMillis(),
                )
                preAuthRoutes(preAuthHandler, connectivityManager)
                pumpStatusRoutes(PumpStatusCache(null, connectivityManager, scope))
            }
        }
    }

    @Test
    fun `GET api-transactions p95 within 150ms`() = testApplication {
        setupRoutes()
        val iterations = 50
        val latencies = LongArray(iterations)

        repeat(iterations) { i ->
            val start = System.currentTimeMillis()
            val response = client.get("/api/v1/transactions")
            latencies[i] = System.currentTimeMillis() - start
            // Stubs return 501 until EA-1.x; accept 501 at this stage
            assertTrue(
                "Unexpected status: ${response.status}",
                response.status == HttpStatusCode.NotImplemented || response.status == HttpStatusCode.OK
            )
        }

        latencies.sort()
        val p95 = latencies[(iterations * 0.95).toInt() - 1]
        assertTrue("GET /api/transactions p95=${p95}ms exceeds 150ms budget", p95 <= 150)
    }

    @Test
    fun `GET api-status p95 within 100ms`() = testApplication {
        setupRoutes()
        val iterations = 50
        val latencies = LongArray(iterations)

        repeat(iterations) { i ->
            val start = System.currentTimeMillis()
            val response = client.get("/api/v1/status")
            latencies[i] = System.currentTimeMillis() - start
            assertTrue(
                "Unexpected status: ${response.status}",
                response.status == HttpStatusCode.NotImplemented || response.status == HttpStatusCode.OK
            )
        }

        latencies.sort()
        val p95 = latencies[(iterations * 0.95).toInt() - 1]
        assertTrue("GET /api/status p95=${p95}ms exceeds 100ms budget", p95 <= 100)
    }
}

package com.fccmiddleware.edge.benchmark

import io.ktor.client.request.*
import io.ktor.client.statement.*
import io.ktor.http.*
import io.ktor.server.testing.*
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

    private fun buildTestApp() = testApplication {
        // Route stubs return 501; latency measurement is still valid for
        // routing overhead benchmarking before real implementation.
        routing {
            com.fccmiddleware.edge.api.transactionRoutes()
            com.fccmiddleware.edge.api.statusRoutes()
            com.fccmiddleware.edge.api.preAuthRoutes()
            com.fccmiddleware.edge.api.pumpStatusRoutes()
        }
    }

    @Test
    fun `GET api-transactions p95 within 150ms`() = testApplication {
        val iterations = 50
        val latencies = LongArray(iterations)

        repeat(iterations) { i ->
            val start = System.currentTimeMillis()
            val response = client.get("/api/transactions")
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
        val iterations = 50
        val latencies = LongArray(iterations)

        repeat(iterations) { i ->
            val start = System.currentTimeMillis()
            val response = client.get("/api/status")
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

package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.adapter.common.PumpStatus
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import io.ktor.http.HttpStatusCode
import io.ktor.server.response.respond
import io.ktor.server.routing.Routing
import io.ktor.server.routing.get
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.async
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import java.time.Instant
import java.util.UUID

/**
 * Pump status endpoint — live or stale pump state from the FCC adapter.
 *
 * Live-response target on healthy LAN: <= 1 s.
 * Stale fallback target: <= 150 ms (served from last-known cache).
 *
 * Single-flight protection: concurrent callers share one live FCC request,
 * preventing fan-out to the FCC when multiple Odoo POS clients poll simultaneously.
 *
 * GET /api/v1/pump-status — returns all pumps; optional ?pumpNumber=N filter
 */
fun Routing.pumpStatusRoutes(cache: PumpStatusCache) {
    get("/api/v1/pump-status") {
        val pumpNumberFilter = call.request.queryParameters["pumpNumber"]?.toIntOrNull()

        val (pumps, stale, fetchedAtUtc, dataAgeSeconds) = cache.get()

        val filtered = if (pumpNumberFilter != null) {
            pumps.filter { it.pumpNumber == pumpNumberFilter }
        } else {
            pumps
        }

        call.respond(
            HttpStatusCode.OK,
            PumpStatusResponse(
                pumps = filtered,
                stale = stale,
                dataAgeSeconds = dataAgeSeconds,
                fetchedAtUtc = fetchedAtUtc,
            )
        )
    }
}

/**
 * Manages pump status retrieval with single-flight protection and stale fallback.
 *
 * Single-flight: if a live FCC fetch is already in progress, concurrent calls
 * wait for the same [Deferred] rather than issuing additional FCC requests.
 *
 * Stale fallback: when FCC is unreachable or the live fetch exceeds [liveTimeoutMs],
 * returns last-known data with [PumpStatusResponse.stale] = true.
 */
class PumpStatusCache(
    private val fccAdapter: IFccAdapter?,
    private val connectivityManager: ConnectivityManager,
    private val scope: CoroutineScope,
    val liveTimeoutMs: Long = 1_000L,
) {
    data class Result(
        val pumps: List<PumpStatus>,
        val stale: Boolean,
        val fetchedAtUtc: String?,
        val dataAgeSeconds: Int?,
    )

    private val mutex = Mutex()
    private var inflight: Deferred<List<PumpStatus>>? = null
    private var cached: List<PumpStatus> = emptyList()
    private var cachedAtMs: Long = 0L

    /**
     * Get current pump status. Returns live data when FCC is reachable,
     * or cached data with [Result.stale] = true on fallback.
     */
    suspend fun get(): Result {
        val fccReachable = connectivityManager.state.value.let {
            it == ConnectivityState.FULLY_ONLINE || it == ConnectivityState.INTERNET_DOWN
        }

        if (!fccReachable || fccAdapter == null) {
            return staleFallback()
        }

        // Ensure only one live FCC call is in flight at a time (single-flight)
        val deferred = mutex.withLock {
            val existing = inflight
            if (existing != null && existing.isActive) {
                existing
            } else {
                val new = scope.async {
                    fccAdapter.getPumpStatus()
                }
                inflight = new
                new
            }
        }

        return try {
            val result = withTimeoutOrNull(liveTimeoutMs) { deferred.await() }
            if (result != null) {
                cached = result
                cachedAtMs = System.currentTimeMillis()
                val fetchedAt = Instant.ofEpochMilli(cachedAtMs).toString()
                Result(pumps = result, stale = false, fetchedAtUtc = fetchedAt, dataAgeSeconds = null)
            } else {
                staleFallback()
            }
        } catch (_: Exception) {
            staleFallback()
        }
    }

    private fun staleFallback(): Result {
        val ageSeconds = if (cachedAtMs > 0L) {
            ((System.currentTimeMillis() - cachedAtMs) / 1_000L).toInt()
        } else null
        val fetchedAt = if (cachedAtMs > 0L) Instant.ofEpochMilli(cachedAtMs).toString() else null
        return Result(pumps = cached, stale = true, fetchedAtUtc = fetchedAt, dataAgeSeconds = ageSeconds)
    }
}

package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.adapter.common.PumpStatus
import com.fccmiddleware.edge.adapter.common.PumpStatusCapability
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import io.ktor.http.HttpStatusCode
import io.ktor.server.response.respond
import io.ktor.server.routing.Routing
import io.ktor.server.routing.get
import kotlinx.coroutines.CoroutineScope
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

        val (pumps, stale, fetchedAtUtc, dataAgeSeconds, capability) = cache.get()

        val filtered = if (pumpNumberFilter != null) {
            pumps.filter { it.pumpNumber == pumpNumberFilter }
        } else {
            pumps
        }

        call.respond(
            HttpStatusCode.OK,
            PumpStatusResponse(
                pumps = filtered,
                capability = capability?.name,
                reason = when (capability) {
                    PumpStatusCapability.NOT_SUPPORTED -> "FCC protocol does not support pump status"
                    PumpStatusCapability.NOT_APPLICABLE -> "Device type does not have pumps"
                    else -> null
                },
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
 * Single-flight: concurrent callers are serialized behind a mutex so only one
 * live FCC fetch is in progress at a time.
 *
 * Stale fallback: when FCC is unreachable or the live fetch exceeds [liveTimeoutMs],
 * returns last-known data with [PumpStatusResponse.stale] = true.
 */
class PumpStatusCache(
    fccAdapter: IFccAdapter? = null,
    private val connectivityManager: ConnectivityManager,
    private val scope: CoroutineScope,
    val liveTimeoutMs: Long = 1_000L,
) {
    /** Late-bound: wired when FCC config becomes available after startup. */
    @Volatile
    internal var fccAdapter: IFccAdapter? = fccAdapter

    data class Result(
        val pumps: List<PumpStatus>,
        val stale: Boolean,
        val fetchedAtUtc: String?,
        val dataAgeSeconds: Int?,
        val capability: PumpStatusCapability?,
    )

    private val mutex = Mutex()
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

        val adapter = fccAdapter
        if (!fccReachable || adapter == null) {
            return staleFallback()
        }

        // Ensure only one live FCC call is in flight at a time (single-flight).
        // Concurrent callers queue behind the mutex instead of fanning out to the FCC.
        return mutex.withLock {
            // AP-024: If cache was freshly populated by a preceding queued caller
            // (within liveTimeoutMs), reuse it instead of making a redundant FCC call.
            val now = System.currentTimeMillis()
            if (cachedAtMs > 0L && (now - cachedAtMs) < liveTimeoutMs) {
                val fetchedAt = Instant.ofEpochMilli(cachedAtMs).toString()
                val ageSeconds = ((now - cachedAtMs) / 1_000L).toInt()
                return@withLock Result(
                    pumps = cached,
                    stale = false,
                    fetchedAtUtc = fetchedAt,
                    dataAgeSeconds = ageSeconds,
                    capability = adapter.pumpStatusCapability,
                )
            }

            val result = try {
                withTimeoutOrNull(liveTimeoutMs) { adapter.getPumpStatus() }
            } catch (_: Exception) {
                null
            }

            if (result != null) {
                cached = result
                cachedAtMs = System.currentTimeMillis()
                val fetchedAt = Instant.ofEpochMilli(cachedAtMs).toString()
                Result(pumps = result, stale = false, fetchedAtUtc = fetchedAt, dataAgeSeconds = null, capability = adapter.pumpStatusCapability)
            } else {
                staleFallback()
            }
        }
    }

    private fun staleFallback(): Result {
        val ageSeconds = if (cachedAtMs > 0L) {
            ((System.currentTimeMillis() - cachedAtMs) / 1_000L).toInt()
        } else null
        val fetchedAt = if (cachedAtMs > 0L) Instant.ofEpochMilli(cachedAtMs).toString() else null
        return Result(pumps = cached, stale = true, fetchedAtUtc = fetchedAt, dataAgeSeconds = ageSeconds, capability = fccAdapter?.pumpStatusCapability)
    }
}

package com.fccmiddleware.edge.api

import android.util.Log
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.preauth.PreAuthHandler
import io.ktor.http.HttpStatusCode
import io.ktor.serialization.kotlinx.json.json
import io.ktor.server.application.Application
import io.ktor.server.application.ApplicationPlugin
import io.ktor.server.application.call
import io.ktor.server.application.createApplicationPlugin
import io.ktor.server.application.install
import io.ktor.server.cio.CIO
import io.ktor.server.cio.CIOApplicationEngine
import io.ktor.server.engine.EmbeddedServer
import io.ktor.server.engine.embeddedServer
import io.ktor.server.plugins.contentnegotiation.ContentNegotiation
import io.ktor.server.plugins.origin
import io.ktor.server.plugins.statuspages.StatusPages
import io.ktor.server.response.respond
import io.ktor.server.routing.routing
import kotlinx.coroutines.CoroutineScope
import kotlinx.serialization.json.Json
import java.security.MessageDigest
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * Embedded Ktor CIO server exposing the Edge Agent local REST API.
 *
 * Default: binds to 127.0.0.1:8585 (localhost only — same-device Odoo POS).
 * LAN mode: binds to 0.0.0.0:8585 when [config.enableLanApi] = true, allowing
 *   secondary HHTs to query the same buffer. LAN requests require [config.lanApiKey].
 *
 * Authentication:
 *   - Requests from 127.0.0.1 / ::1 bypass API key check (same-device Odoo POS).
 *   - LAN requests require `X-Api-Key: <key>` header (constant-time comparison).
 *
 * Architecture rule: localhost mode must never regress when LAN mode is enabled.
 */
class LocalApiServer(
    val config: LocalApiServerConfig,
    private val transactionDao: TransactionBufferDao,
    private val syncStateDao: SyncStateDao,
    private val connectivityManager: ConnectivityManager,
    private val preAuthHandler: PreAuthHandler,
    private val fccAdapter: IFccAdapter?,
    private val serviceScope: CoroutineScope,
    private val ingestionOrchestrator: IngestionOrchestrator? = null,
    private val serviceStartMs: Long = System.currentTimeMillis(),
    private val deviceId: String = "00000000-0000-0000-0000-000000000000",
    private val siteCode: String = "UNPROVISIONED",
    private val agentVersion: String = "1.0.0",
) {
    data class LocalApiServerConfig(
        val port: Int = 8585,
        /** true: bind to 0.0.0.0 for primary-HHT LAN multi-device mode. */
        val enableLanApi: Boolean = false,
        /** Required when [enableLanApi] = true. Validated with constant-time comparison. */
        val lanApiKey: String? = null,
        /** Max requests per second for mutating endpoints (preauth, pull). */
        val rateLimitMutatingRps: Int = 10,
        /** Max requests per second for read endpoints (transactions, status, pump-status). */
        val rateLimitReadRps: Int = 30,
    ) {
        val bindAddress: String get() = if (enableLanApi) "0.0.0.0" else "127.0.0.1"
    }

    private var server: EmbeddedServer<CIOApplicationEngine, CIOApplicationEngine.Configuration>? = null
    private val pumpStatusCache = PumpStatusCache(
        fccAdapter = fccAdapter,
        connectivityManager = connectivityManager,
        scope = serviceScope,
    )
    private val tag = "LocalApiServer"

    fun start() {
        server = embeddedServer(CIO, port = config.port, host = config.bindAddress) {
            configureContentNegotiation()
            configureLanApiKeyAuth()
            configureRateLimiting()
            configureStatusPages()
            configureRouting()
        }.also {
            it.start(wait = false)
            Log.i(tag, "Local API server started on ${config.bindAddress}:${config.port} (lanApi=${config.enableLanApi})")
        }
    }

    fun stop() {
        server?.stop(1_000, 2_000)
        Log.i(tag, "Local API server stopped")
    }

    // -------------------------------------------------------------------------
    // Ktor configuration
    // -------------------------------------------------------------------------

    private fun Application.configureContentNegotiation() {
        install(ContentNegotiation) {
            json(Json {
                prettyPrint = false
                isLenient = false
                ignoreUnknownKeys = true
                encodeDefaults = true
            })
        }
    }

    private fun Application.configureLanApiKeyAuth() {
        if (!config.enableLanApi) return
        val storedKey = config.lanApiKey
        if (storedKey == null) {
            // LAN mode requested but no API key configured — reject ALL non-localhost
            // requests to prevent unauthenticated access from the local network.
            Log.e(tag, "LAN API enabled but lanApiKey is null — all LAN requests will be rejected")
            install(LanApiBlockPlugin)
            return
        }

        install(LanApiKeyAuthPlugin(storedKey))
    }

    private fun Application.configureRateLimiting() {
        install(
            RateLimitPlugin(
                mutatingRps = config.rateLimitMutatingRps,
                readRps = config.rateLimitReadRps,
            )
        )
    }

    private fun Application.configureStatusPages() {
        install(StatusPages) {
            exception<Throwable> { call, cause ->
                Log.e(tag, "Unhandled error in local API", cause)
                call.respond(
                    HttpStatusCode.InternalServerError,
                    ErrorResponse(
                        errorCode = "INTERNAL_ERROR",
                        message = cause.message ?: "Internal error",
                        traceId = UUID.randomUUID().toString(),
                        timestamp = Instant.now().toString(),
                    )
                )
            }
        }
    }

    private fun Application.configureRouting() {
        routing {
            transactionRoutes(
                dao = transactionDao,
                ingestionOrchestrator = ingestionOrchestrator,
                connectivityManager = connectivityManager,
                lanApiKey = config.lanApiKey,
                enableLanApi = config.enableLanApi,
            )
            preAuthRoutes(
                handler = preAuthHandler,
                connectivityManager = connectivityManager,
                lanApiKey = config.lanApiKey,
                enableLanApi = config.enableLanApi,
            )
            pumpStatusRoutes(pumpStatusCache)
            statusRoutes(
                connectivityManager = connectivityManager,
                transactionDao = transactionDao,
                syncStateDao = syncStateDao,
                agentVersion = agentVersion,
                deviceId = deviceId,
                siteCode = siteCode,
                serviceStartMs = serviceStartMs,
            )
        }
    }
}

// -------------------------------------------------------------------------
// Route-level auth verification (defense-in-depth)
// -------------------------------------------------------------------------

/**
 * Defense-in-depth auth check callable from individual route handlers.
 *
 * Returns true if the request is authorized (localhost or valid API key).
 * If unauthorized, responds with 401 and returns false — the caller must `return`.
 *
 * This supplements the global [LanApiKeyAuthPlugin] so that even if the global
 * plugin is misconfigured or bypassed, each route independently verifies access.
 */
internal suspend fun routeRequiresAuth(
    call: io.ktor.server.application.ApplicationCall,
    lanApiKey: String?,
    enableLanApi: Boolean,
): Boolean {
    val remoteAddress = call.request.origin.remoteHost
    val isLocalhost = remoteAddress == "127.0.0.1" ||
        remoteAddress == "::1" ||
        remoteAddress == "0:0:0:0:0:0:0:1"

    // Localhost is always trusted (same-device Odoo POS)
    if (isLocalhost) return true

    // Non-localhost: LAN mode must be enabled AND key must match
    if (!enableLanApi) {
        call.respond(
            HttpStatusCode.Forbidden,
            ErrorResponse(
                errorCode = "LAN_ACCESS_DISABLED",
                message = "LAN access is not enabled on this agent",
                traceId = UUID.randomUUID().toString(),
                timestamp = Instant.now().toString(),
            )
        )
        return false
    }

    if (lanApiKey == null) {
        call.respond(
            HttpStatusCode.Forbidden,
            ErrorResponse(
                errorCode = "LAN_API_KEY_NOT_CONFIGURED",
                message = "LAN access is enabled but no API key is configured",
                traceId = UUID.randomUUID().toString(),
                timestamp = Instant.now().toString(),
            )
        )
        return false
    }

    val headerKey = call.request.headers["X-Api-Key"]
    if (headerKey == null || !lanApiKeyEquals(headerKey, lanApiKey)) {
        call.respond(
            HttpStatusCode.Unauthorized,
            ErrorResponse(
                errorCode = "UNAUTHORIZED",
                message = "Valid X-Api-Key header required for LAN access",
                traceId = UUID.randomUUID().toString(),
                timestamp = Instant.now().toString(),
            )
        )
        return false
    }

    return true
}

// -------------------------------------------------------------------------
// LAN API key authentication Ktor plugin
// -------------------------------------------------------------------------

/**
 * Ktor application plugin that enforces API key authentication for LAN clients.
 *
 * Requests from localhost (127.0.0.1, ::1) bypass the check.
 * All other clients must include `X-Api-Key: <key>` matching the stored key.
 * Comparison is constant-time to prevent timing side-channels.
 */
private fun LanApiKeyAuthPlugin(storedKey: String): ApplicationPlugin<Unit> =
    createApplicationPlugin("LanApiKeyAuth") {
        onCall { call ->
            val remoteAddress = call.request.origin.remoteHost
            val isLocalhost = remoteAddress == "127.0.0.1" ||
                remoteAddress == "::1" ||
                remoteAddress == "0:0:0:0:0:0:0:1"

            if (!isLocalhost) {
                val headerKey = call.request.headers["X-Api-Key"]
                if (headerKey == null || !lanApiKeyEquals(headerKey, storedKey)) {
                    call.respond(
                        HttpStatusCode.Unauthorized,
                        ErrorResponse(
                            errorCode = "UNAUTHORIZED",
                            message = "Valid X-Api-Key header required for LAN access",
                            traceId = UUID.randomUUID().toString(),
                            timestamp = Instant.now().toString(),
                        )
                    )
                }
            }
        }
    }

/**
 * Ktor plugin that rejects ALL non-localhost requests when LAN mode is enabled
 * but no API key is configured. This is a safety net — prevents unauthenticated
 * access until a proper lanApiKey is provisioned.
 */
private val LanApiBlockPlugin: ApplicationPlugin<Unit> =
    createApplicationPlugin("LanApiBlock") {
        onCall { call ->
            val remoteAddress = call.request.origin.remoteHost
            val isLocalhost = remoteAddress == "127.0.0.1" ||
                remoteAddress == "::1" ||
                remoteAddress == "0:0:0:0:0:0:0:1"

            if (!isLocalhost) {
                call.respond(
                    HttpStatusCode.Forbidden,
                    ErrorResponse(
                        errorCode = "LAN_API_KEY_NOT_CONFIGURED",
                        message = "LAN access is enabled but no API key is configured. All non-localhost requests are rejected.",
                        traceId = UUID.randomUUID().toString(),
                        timestamp = Instant.now().toString(),
                    )
                )
            }
        }
    }

/**
 * Constant-time string equality check to prevent timing attacks on the API key.
 */
private fun lanApiKeyEquals(provided: String, stored: String): Boolean =
    MessageDigest.isEqual(
        provided.toByteArray(Charsets.UTF_8),
        stored.toByteArray(Charsets.UTF_8),
    )

// -------------------------------------------------------------------------
// Rate limiting — per-endpoint sliding window (1-second resolution)
// -------------------------------------------------------------------------

/**
 * Simple per-bucket rate limiter using a sliding 1-second window.
 *
 * Buckets:
 *   - "mutating" — POST endpoints that cause FCC interaction (preauth, pull)
 *   - "read"     — GET endpoints and acknowledge (local-only, idempotent)
 *
 * When the limit is exceeded, responds 429 with a Retry-After: 1 header.
 * Thread-safe via AtomicLong (window epoch-second) + AtomicInteger (counter).
 */
private class SlidingWindowCounter(private val maxPerSecond: Int) {
    private val windowSecond = AtomicLong(0)
    private val counter = AtomicInteger(0)

    /** Returns true if the request is allowed, false if rate-limited. */
    fun tryAcquire(): Boolean {
        val nowSecond = System.currentTimeMillis() / 1000L
        val currentWindow = windowSecond.get()
        if (nowSecond != currentWindow) {
            // New window — reset. Race is benign: worst case two threads both reset,
            // which slightly under-counts for one second (safe direction).
            if (windowSecond.compareAndSet(currentWindow, nowSecond)) {
                counter.set(1)
                return true
            }
        }
        return counter.incrementAndGet() <= maxPerSecond
    }
}

/** Mutating paths that interact with the FCC or modify state. */
private val MUTATING_PATHS = setOf(
    "/api/v1/preauth",
    "/api/v1/preauth/cancel",
    "/api/v1/transactions/pull",
)

private fun RateLimitPlugin(
    mutatingRps: Int,
    readRps: Int,
): ApplicationPlugin<Unit> = createApplicationPlugin("RateLimit") {
    val mutatingLimiter = SlidingWindowCounter(mutatingRps)
    val readLimiter = SlidingWindowCounter(readRps)

    onCall { call ->
        val path = call.request.local.uri
        val limiter = if (path in MUTATING_PATHS) mutatingLimiter else readLimiter

        if (!limiter.tryAcquire()) {
            call.response.headers.append("Retry-After", "1")
            call.respond(
                HttpStatusCode.TooManyRequests,
                ErrorResponse(
                    errorCode = "RATE_LIMITED",
                    message = "Too many requests. Try again in 1 second.",
                    traceId = UUID.randomUUID().toString(),
                    timestamp = Instant.now().toString(),
                )
            )
        }
    }
}

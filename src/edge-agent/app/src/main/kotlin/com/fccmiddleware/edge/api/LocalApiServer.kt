package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.config.ConfigManager
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
import io.ktor.server.application.ApplicationCallPipeline
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
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import com.fccmiddleware.edge.logging.CorrelationIdElement
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
 *   - Optionally, [LocalApiServerConfig.lanApiIpAllowlist] restricts LAN access to a
 *     specific set of source IPs (defense-in-depth on top of the API key).
 *
 * Security note: LAN mode traffic is plain HTTP (no TLS). It must only be used on
 *   physically isolated or trusted networks (e.g., dedicated POS LAN segment). Do NOT
 *   enable LAN mode on open or shared WiFi networks — API key material and transaction
 *   data would be visible to any device on the same network segment.
 *
 * Architecture rule: localhost mode must never regress when LAN mode is enabled.
 */
class LocalApiServer(
    config: LocalApiServerConfig,
    private val transactionDao: TransactionBufferDao,
    private val syncStateDao: SyncStateDao,
    private val connectivityManager: ConnectivityManager,
    private val preAuthHandler: PreAuthHandler,
    private val configManager: ConfigManager,
    fccAdapter: IFccAdapter? = null,
    private val serviceScope: CoroutineScope,
    private val ingestionOrchestrator: IngestionOrchestrator? = null,
    private val serviceStartMs: Long = System.currentTimeMillis(),
    deviceId: String = "00000000-0000-0000-0000-000000000000",
    siteCode: String = "UNPROVISIONED",
    agentVersion: String = "1.0.0",
) {
    /** Late-bound: wired via [wireFccAdapter] when FCC config becomes available after startup. */
    private var fccAdapter: IFccAdapter? = fccAdapter
    @Volatile
    var config: LocalApiServerConfig = config
        private set
    @Volatile
    private var deviceId: String = deviceId
    @Volatile
    private var siteCode: String = siteCode
    @Volatile
    private var agentVersion: String = agentVersion

    data class LocalApiServerConfig(
        val port: Int = 8585,
        /** true: bind to 0.0.0.0 for primary-HHT LAN multi-device mode. */
        val enableLanApi: Boolean = false,
        /** Required when [enableLanApi] = true. Validated with constant-time comparison. */
        val lanApiKey: String? = null,
        /**
         * Optional source-IP allowlist for LAN mode (defense-in-depth, S-004).
         * When non-null and non-empty, only requests originating from one of these IP addresses
         * will be accepted (in addition to the API key check). Localhost is always allowed
         * regardless of this list. Set to null to disable allowlist enforcement.
         *
         * Example: setOf("192.168.1.50", "192.168.1.51")
         */
        val lanApiIpAllowlist: Set<String>? = null,
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

    /** Wire the FCC adapter into this server and its pump status cache. */
    internal fun wireFccAdapter(adapter: IFccAdapter?) {
        fccAdapter = adapter
        pumpStatusCache.fccAdapter = adapter
    }

    internal fun reconfigure(
        config: LocalApiServerConfig,
        deviceId: String = this.deviceId,
        siteCode: String = this.siteCode,
        agentVersion: String = this.agentVersion,
    ) {
        val shouldRestart = server != null
        this.config = config
        this.deviceId = deviceId
        this.siteCode = siteCode
        this.agentVersion = agentVersion
        if (shouldRestart) {
            start()
        }
    }

    fun start() {
        server?.stop(1_000, 2_000)
        server = embeddedServer(CIO, port = config.port, host = config.bindAddress) {
            configureCorrelationId()
            configureContentNegotiation()
            configureLanApiKeyAuth()
            configureRateLimiting()
            configureStatusPages()
            configureRouting()
        }.also {
            it.start(wait = false)
            AppLogger.i(tag, "Local API server started on ${config.bindAddress}:${config.port} (lanApi=${config.enableLanApi})")
            if (config.enableLanApi) {
                AppLogger.w(tag, "LAN API mode is active — traffic is plain HTTP (no TLS). " +
                    "Ensure this device is on a physically isolated or trusted network segment.")
                if (!config.lanApiIpAllowlist.isNullOrEmpty()) {
                    AppLogger.i(tag, "LAN API IP allowlist active: ${config.lanApiIpAllowlist}")
                }
            }
        }
    }

    fun stop() {
        server?.stop(1_000, 2_000)
        AppLogger.i(tag, "Local API server stopped")
    }

    // -------------------------------------------------------------------------
    // Ktor configuration
    // -------------------------------------------------------------------------

    /**
     * Phase 5: Extract X-Correlation-Id from incoming requests and scope it to
     * the request's coroutine via [CorrelationIdElement] (AF-002).
     *
     * Each request gets its own correlation ID that is propagated through the
     * coroutine context, preventing concurrent requests from overwriting each
     * other's IDs on the shared logger.
     */
    private fun Application.configureCorrelationId() {
        intercept(ApplicationCallPipeline.Setup) {
            val incomingId = call.request.headers["X-Correlation-Id"]
            val correlationId = incomingId ?: UUID.randomUUID().toString()
            call.response.headers.append("X-Correlation-Id", correlationId)
            withContext(CorrelationIdElement(correlationId)) {
                proceed()
            }
        }
    }

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
            AppLogger.e(tag, "LAN API enabled but lanApiKey is null — all LAN requests will be rejected")
            install(LanApiBlockPlugin)
            return
        }

        install(LanApiKeyAuthPlugin(storedKey, config.lanApiIpAllowlist))
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
                AppLogger.e(tag, "Unhandled error in local API", cause)
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
                lanApiIpAllowlist = config.lanApiIpAllowlist,
            )
            preAuthRoutes(
                handler = preAuthHandler,
                connectivityManager = connectivityManager,
                lanApiKey = config.lanApiKey,
                enableLanApi = config.enableLanApi,
                lanApiIpAllowlist = config.lanApiIpAllowlist,
            )
            pumpStatusRoutes(pumpStatusCache)
            statusRoutes(
                connectivityManager = connectivityManager,
                transactionDao = transactionDao,
                syncStateDao = syncStateDao,
                configManager = configManager,
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
 * Returns true if the request is authorized (localhost or valid API key + optional IP allowlist).
 * If unauthorized, responds with 401/403 and returns false — the caller must `return`.
 *
 * This supplements the global [LanApiKeyAuthPlugin] so that even if the global
 * plugin is misconfigured or bypassed, each route independently verifies access.
 */
internal suspend fun routeRequiresAuth(
    call: io.ktor.server.application.ApplicationCall,
    lanApiKey: String?,
    enableLanApi: Boolean,
    lanApiIpAllowlist: Set<String>? = null,
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

    // IP allowlist check (defense-in-depth, S-004)
    if (!lanApiIpAllowlist.isNullOrEmpty() && remoteAddress !in lanApiIpAllowlist) {
        call.respond(
            HttpStatusCode.Forbidden,
            ErrorResponse(
                errorCode = "IP_NOT_ALLOWED",
                message = "Source IP is not in the LAN API allowlist",
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
 * When [ipAllowlist] is non-null and non-empty, only requests from those source IPs
 * are accepted (defense-in-depth on top of the API key, S-004).
 */
private fun LanApiKeyAuthPlugin(storedKey: String, ipAllowlist: Set<String>?): ApplicationPlugin<Unit> =
    createApplicationPlugin("LanApiKeyAuth") {
        onCall { call ->
            val remoteAddress = call.request.origin.remoteHost
            val isLocalhost = remoteAddress == "127.0.0.1" ||
                remoteAddress == "::1" ||
                remoteAddress == "0:0:0:0:0:0:0:1"

            if (!isLocalhost) {
                // IP allowlist check (S-004)
                if (!ipAllowlist.isNullOrEmpty() && remoteAddress !in ipAllowlist) {
                    call.respond(
                        HttpStatusCode.Forbidden,
                        ErrorResponse(
                            errorCode = "IP_NOT_ALLOWED",
                            message = "Source IP is not in the LAN API allowlist",
                            traceId = UUID.randomUUID().toString(),
                            timestamp = Instant.now().toString(),
                        )
                    )
                    return@onCall
                }

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
                    return@onCall
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
                return@onCall
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
            return@onCall
        }
    }
}

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
import io.ktor.server.plugins.statuspages.StatusPages
import io.ktor.server.request.local
import io.ktor.server.response.respond
import io.ktor.server.routing.routing
import kotlinx.coroutines.CoroutineScope
import kotlinx.serialization.json.Json
import java.security.MessageDigest
import java.time.Instant
import java.util.UUID

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
            })
        }
    }

    private fun Application.configureLanApiKeyAuth() {
        if (!config.enableLanApi) return
        val storedKey = config.lanApiKey ?: return

        install(LanApiKeyAuthPlugin(storedKey))
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
            transactionRoutes(transactionDao, ingestionOrchestrator, connectivityManager)
            preAuthRoutes(preAuthHandler, connectivityManager)
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
            val remoteAddress = call.request.local.remoteAddress
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
 * Constant-time string equality check to prevent timing attacks on the API key.
 */
private fun lanApiKeyEquals(provided: String, stored: String): Boolean =
    MessageDigest.isEqual(
        provided.toByteArray(Charsets.UTF_8),
        stored.toByteArray(Charsets.UTF_8),
    )

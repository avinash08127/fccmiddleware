package com.fccmiddleware.agent.api

import io.ktor.serialization.kotlinx.json.*
import io.ktor.server.application.*
import io.ktor.server.cio.*
import io.ktor.server.engine.*
import io.ktor.server.plugins.contentnegotiation.*
import io.ktor.server.plugins.statuspages.*
import io.ktor.server.response.*
import io.ktor.server.routing.*
import kotlinx.serialization.json.Json
import timber.log.Timber

class LocalApiServer(private val port: Int = 8585) {

    private var server: EmbeddedServer<CIOApplicationEngine, CIOApplicationEngine.Configuration>? = null

    fun start() {
        server = embeddedServer(CIO, port = port) {
            configureContentNegotiation()
            configureStatusPages()
            configureRouting()
        }.also {
            it.start(wait = false)
            Timber.i("Local API server started on port $port")
        }
    }

    fun stop() {
        server?.stop(1000, 2000)
        Timber.i("Local API server stopped")
    }

    private fun Application.configureContentNegotiation() {
        install(ContentNegotiation) {
            json(Json {
                prettyPrint = false
                isLenient = false
                ignoreUnknownKeys = true
            })
        }
    }

    private fun Application.configureStatusPages() {
        install(StatusPages) {
            exception<Throwable> { call, cause ->
                Timber.e(cause, "Unhandled error in local API")
                call.respondText("Internal Server Error", status = io.ktor.http.HttpStatusCode.InternalServerError)
            }
        }
    }

    private fun Application.configureRouting() {
        routing {
            get("/api/health") {
                call.respondText("OK")
            }
            // TODO: register route extensions
            // transactionRoutes(...)
            // preAuthRoutes(...)
            // pumpStatusRoutes(...)
        }
    }
}

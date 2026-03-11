package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.adapter.common.PreAuthCommand
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.preauth.PreAuthHandler
import io.ktor.http.HttpStatusCode
import io.ktor.server.request.receive
import io.ktor.server.response.respond
import io.ktor.server.routing.Routing
import io.ktor.server.routing.post
import java.time.Instant
import java.util.UUID

/**
 * Pre-auth endpoints — pre-authorisation lifecycle (Odoo POS → FCC via Edge Agent).
 * All routes use /api/v1/ prefix per edge-agent-local-api.yaml.
 *
 * TOP LATENCY PATH: POST /api/v1/preauth must respond based on LAN-only work.
 * Cloud forwarding is always async and NEVER on the request path.
 * p95 local API overhead: <= 150 ms before FCC call time.
 * p95 end-to-end on healthy FCC LAN: <= 1.5 s; p99 <= 3 s.
 *
 * POST /api/v1/preauth        — submit pre-auth request (delegates to PreAuthHandler)
 * POST /api/v1/preauth/cancel — cancel an active pre-auth by Odoo order ID
 */
fun Routing.preAuthRoutes(
    handler: PreAuthHandler,
    connectivityManager: ConnectivityManager,
) {

    /**
     * POST /api/v1/preauth
     *
     * Accepts a PreAuthCommand JSON body and returns the FCC response.
     * Rejects immediately if FCC is unreachable (503) — Odoo POS must handle this.
     */
    post("/api/v1/preauth") {
        val fccState = connectivityManager.state.value
        val fccReachable = fccState == ConnectivityState.FULLY_ONLINE ||
            fccState == ConnectivityState.INTERNET_DOWN

        if (!fccReachable) {
            call.respond(
                HttpStatusCode.ServiceUnavailable,
                ErrorResponse(
                    errorCode = "FCC_UNREACHABLE",
                    message = "FCC is not reachable. Pre-auth rejected. Current state: $fccState",
                    traceId = UUID.randomUUID().toString(),
                    timestamp = Instant.now().toString(),
                )
            )
            return@post
        }

        val command = try {
            call.receive<PreAuthCommand>()
        } catch (_: Exception) {
            call.respond(
                HttpStatusCode.BadRequest,
                ErrorResponse(
                    errorCode = "INVALID_REQUEST",
                    message = "Request body must match PreAuthCommand schema",
                    traceId = UUID.randomUUID().toString(),
                    timestamp = Instant.now().toString(),
                )
            )
            return@post
        }

        val result = handler.handle(command)
        call.respond(HttpStatusCode.OK, result)
    }

    /**
     * POST /api/v1/preauth/cancel
     *
     * Cancels an active pre-auth by Odoo order ID + site code.
     * Returns 200 even if the pre-auth was not found (idempotent).
     */
    post("/api/v1/preauth/cancel") {
        val request = try {
            call.receive<CancelPreAuthRequest>()
        } catch (_: Exception) {
            call.respond(
                HttpStatusCode.BadRequest,
                ErrorResponse(
                    errorCode = "INVALID_REQUEST",
                    message = "Request body must be JSON with 'odooOrderId' and 'siteCode'",
                    traceId = UUID.randomUUID().toString(),
                    timestamp = Instant.now().toString(),
                )
            )
            return@post
        }

        val result = handler.cancel(request.odooOrderId, request.siteCode)
        call.respond(
            HttpStatusCode.OK,
            CancelPreAuthResponse(success = result.success, message = result.message)
        )
    }
}

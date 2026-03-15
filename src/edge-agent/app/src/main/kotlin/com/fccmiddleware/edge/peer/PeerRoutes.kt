package com.fccmiddleware.edge.peer

import io.ktor.http.HttpStatusCode
import io.ktor.server.application.call
import io.ktor.server.request.receive
import io.ktor.server.response.respond
import io.ktor.server.routing.Route
import io.ktor.server.routing.get
import io.ktor.server.routing.post
import kotlinx.serialization.Serializable

/**
 * Ktor routing extension that mounts all peer-to-peer HA API endpoints.
 *
 * Endpoints:
 *   GET  /peer/health            — Health response for peer liveness checks
 *   POST /peer/heartbeat         — Receive heartbeat from a peer
 *   POST /peer/claim-leadership  — Receive leadership claim from a candidate
 *   GET  /peer/bootstrap         — Full snapshot for initial replication (stub)
 *   GET  /peer/sync              — Incremental delta sync (stub)
 *   POST /peer/proxy/preauth     — Forward pre-auth to primary (stub)
 *   GET  /peer/proxy/pump-status — Get pump status from primary (stub)
 *
 * All endpoints are HMAC-authenticated by the [PeerHmacAuthPlugin] installed
 * at the server level. Individual routes do not need additional auth checks.
 */
fun Route.peerRoutes(coordinator: PeerCoordinator) {

    // ── Health ──────────────────────────────────────────────────────────────

    get("/peer/health") {
        val response = coordinator.buildHealthResponse()
        call.respond(HttpStatusCode.OK, response)
    }

    // ── Heartbeat ───────────────────────────────────────────────────────────

    post("/peer/heartbeat") {
        val request = call.receive<PeerHeartbeatRequest>()
        val response = coordinator.handleIncomingHeartbeat(request)
        call.respond(HttpStatusCode.OK, response)
    }

    // ── Leadership Claim ────────────────────────────────────────────────────

    post("/peer/claim-leadership") {
        val request = call.receive<PeerLeadershipClaimRequest>()
        val response = coordinator.handleLeadershipClaim(request)
        val status = if (response.accepted) HttpStatusCode.OK else HttpStatusCode.Conflict
        call.respond(status, response)
    }

    // ── Bootstrap (stub — 501 Not Implemented) ──────────────────────────────

    get("/peer/bootstrap") {
        call.respond(
            HttpStatusCode.NotImplemented,
            StubResponse(message = "Bootstrap endpoint not yet implemented"),
        )
    }

    // ── Delta Sync (stub — 501 Not Implemented) ─────────────────────────────

    get("/peer/sync") {
        call.respond(
            HttpStatusCode.NotImplemented,
            StubResponse(message = "Delta sync endpoint not yet implemented"),
        )
    }

    // ── Pre-Auth Proxy (stub — 501 Not Implemented) ─────────────────────────

    post("/peer/proxy/preauth") {
        call.respond(
            HttpStatusCode.NotImplemented,
            StubResponse(message = "Pre-auth proxy endpoint not yet implemented"),
        )
    }

    // ── Pump Status Proxy (stub — 501 Not Implemented) ──────────────────────

    get("/peer/proxy/pump-status") {
        call.respond(
            HttpStatusCode.NotImplemented,
            StubResponse(message = "Pump status proxy endpoint not yet implemented"),
        )
    }
}

@Serializable
data class StubResponse(
    val message: String,
)

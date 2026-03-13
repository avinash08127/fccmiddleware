package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import io.ktor.http.HttpStatusCode
import io.ktor.server.request.receive
import io.ktor.server.response.respond
import io.ktor.server.routing.Routing
import io.ktor.server.routing.get
import io.ktor.server.routing.post
import java.time.Instant
import java.util.UUID

/**
 * Transaction endpoints — locally buffered transaction access and on-demand FCC pull.
 * All routes use /api/v1/ prefix per edge-agent-local-api.yaml.
 *
 * GET  /api/v1/transactions             — list buffered transactions (p95 <= 150 ms at 30k records)
 * GET  /api/v1/transactions/{id}        — get single buffered transaction by middleware UUID
 * POST /api/v1/transactions/acknowledge — Odoo POS marks a batch of transactions as consumed
 * POST /api/v1/transactions/pull        — on-demand FCC pull (EA-2.7)
 *
 * Never depends on live FCC access for read/acknowledge paths. Excludes SYNCED_TO_ODOO
 * records per §5.3 of the state machine spec to prevent Odoo double-consumption.
 */
fun Routing.transactionRoutes(
    dao: TransactionBufferDao,
    ingestionOrchestrator: IngestionOrchestrator? = null,
    connectivityManager: ConnectivityManager? = null,
    lanApiKey: String? = null,
    enableLanApi: Boolean = false,
    lanApiIpAllowlist: Set<String>? = null,
) {

    /**
     * GET /api/v1/transactions
     *
     * Query params:
     *   pumpNumber (Int, optional)   — filter by physical FCC pump number
     *   since      (ISO 8601, opt.)  — return records with completedAt >= since
     *   limit      (Int, default 50, max 100)
     *   offset     (Int, default 0)
     */
    get("/api/v1/transactions") {
        if (!routeRequiresAuth(call, lanApiKey, enableLanApi, lanApiIpAllowlist)) return@get
        val pumpNumber = call.request.queryParameters["pumpNumber"]?.toIntOrNull()?.takeIf { it >= 0 }
        val sinceParam = call.request.queryParameters["since"]
        val limit = (call.request.queryParameters["limit"]?.toIntOrNull() ?: 50).coerceIn(1, 100)
        val offset = (call.request.queryParameters["offset"]?.toIntOrNull() ?: 0).coerceAtLeast(0)

        if (sinceParam != null) {
            try {
                Instant.parse(sinceParam)
            } catch (_: Exception) {
                call.respond(
                    HttpStatusCode.BadRequest,
                    ErrorResponse(
                        errorCode = "INVALID_PARAMETER",
                        message = "Parameter 'since' must be a valid ISO 8601 UTC timestamp (e.g. 2024-01-15T10:00:00Z)",
                        traceId = UUID.randomUUID().toString(),
                        timestamp = Instant.now().toString(),
                    )
                )
                return@get
            }
        }

        val entities = when {
            pumpNumber != null && sinceParam != null ->
                dao.getForLocalApiByPumpSince(pumpNumber, sinceParam, limit, offset)
            pumpNumber != null ->
                dao.getForLocalApiByPump(pumpNumber, limit, offset)
            sinceParam != null ->
                dao.getForLocalApiSince(sinceParam, limit, offset)
            else ->
                dao.getForLocalApi(limit, offset)
        }

        val total = when {
            pumpNumber != null && sinceParam != null ->
                dao.countForLocalApiByPumpSince(pumpNumber, sinceParam)
            pumpNumber != null ->
                dao.countForLocalApiByPump(pumpNumber)
            sinceParam != null ->
                dao.countForLocalApiSince(sinceParam)
            else ->
                dao.countForLocalApi()
        }

        call.respond(
            HttpStatusCode.OK,
            TransactionListResponse(
                transactions = entities.map { LocalTransaction.from(it) },
                total = total,
                limit = limit,
                offset = offset,
            )
        )
    }

    /**
     * GET /api/v1/transactions/{id}
     *
     * Returns full transaction detail. 404 if not found.
     */
    get("/api/v1/transactions/{id}") {
        if (!routeRequiresAuth(call, lanApiKey, enableLanApi, lanApiIpAllowlist)) return@get
        val id = call.parameters["id"] ?: run {
            call.respond(
                HttpStatusCode.BadRequest,
                ErrorResponse(
                    errorCode = "MISSING_PARAMETER",
                    message = "Path parameter 'id' is required",
                    traceId = UUID.randomUUID().toString(),
                    timestamp = Instant.now().toString(),
                )
            )
            return@get
        }

        val entity = dao.getById(id)
        if (entity == null) {
            call.respond(
                HttpStatusCode.NotFound,
                ErrorResponse(
                    errorCode = "NOT_FOUND",
                    message = "Transaction '$id' not found",
                    traceId = UUID.randomUUID().toString(),
                    timestamp = Instant.now().toString(),
                )
            )
            return@get
        }

        call.respond(HttpStatusCode.OK, LocalTransaction.from(entity))
    }

    /**
     * POST /api/v1/transactions/acknowledge
     *
     * Odoo POS marks a batch of transactions as locally consumed.
     * Local-only operation — does NOT change sync_status (that is cloud-driven).
     * Idempotent: always returns 200 with count of recognized IDs.
     */
    post("/api/v1/transactions/acknowledge") {
        if (!routeRequiresAuth(call, lanApiKey, enableLanApi, lanApiIpAllowlist)) return@post
        val request = try {
            call.receive<BatchAcknowledgeRequest>()
        } catch (_: Exception) {
            call.respond(
                HttpStatusCode.BadRequest,
                ErrorResponse(
                    errorCode = "INVALID_REQUEST",
                    message = "Request body must be JSON with 'transactionIds' array",
                    traceId = UUID.randomUUID().toString(),
                    timestamp = Instant.now().toString(),
                )
            )
            return@post
        }

        if (request.transactionIds.isEmpty()) {
            call.respond(HttpStatusCode.OK, BatchAcknowledgeResponse(acknowledged = 0))
            return@post
        }

        // AF-022: Actually mark transactions as acknowledged so they are excluded from
        // subsequent GET /api/v1/transactions calls, preventing POS double-consumption.
        val now = Instant.now().toString()
        val acknowledged = dao.markAcknowledged(request.transactionIds, now)
        call.respond(HttpStatusCode.OK, BatchAcknowledgeResponse(acknowledged = acknowledged))
    }

    /**
     * POST /api/v1/transactions/pull
     *
     * On-demand FCC pull — Odoo POS can call this to surface a just-completed dispense
     * without waiting for the next scheduled poll cycle (EA-2.7 / REQ-15.7).
     *
     * The pull is serialized with the background poller via [IngestionOrchestrator.pollMutex]
     * so manual and scheduled polls never race or corrupt cursor state.
     *
     * Request body (optional JSON):
     *   pumpNumber (Int, optional) — informational; logged for diagnostics only.
     *                                All transactions since the last cursor are fetched.
     *
     * Responses:
     *   200 — pull completed; body contains [ManualPullResponse] with counts.
     *   503 — FCC is unreachable or ingestion is not yet configured.
     */
    post("/api/v1/transactions/pull") {
        if (!routeRequiresAuth(call, lanApiKey, enableLanApi, lanApiIpAllowlist)) return@post
        // Check FCC reachability via ConnectivityManager when available
        val cm = connectivityManager
        if (cm != null) {
            val state = cm.state.value
            val fccReachable = state == ConnectivityState.FULLY_ONLINE ||
                state == ConnectivityState.INTERNET_DOWN
            if (!fccReachable) {
                call.respond(
                    HttpStatusCode.ServiceUnavailable,
                    ErrorResponse(
                        errorCode = "FCC_UNREACHABLE",
                        message = "FCC is not reachable (connectivity state: ${state.name}). Manual pull unavailable.",
                        traceId = UUID.randomUUID().toString(),
                        timestamp = Instant.now().toString(),
                    )
                )
                return@post
            }
        }

        // Parse optional pumpNumber from request body (body may be absent)
        val pullRequest = try {
            call.receive<ManualPullRequest>()
        } catch (_: Exception) {
            ManualPullRequest()
        }

        val orchestrator = ingestionOrchestrator
        if (orchestrator == null) {
            call.respond(
                HttpStatusCode.ServiceUnavailable,
                ErrorResponse(
                    errorCode = "FCC_UNREACHABLE",
                    message = "FCC adapter is not yet configured. Manual pull unavailable.",
                    traceId = UUID.randomUUID().toString(),
                    timestamp = Instant.now().toString(),
                )
            )
            return@post
        }

        val triggeredAt = Instant.now().toString()
        val result = orchestrator.pollNow(pumpNumber = pullRequest.pumpNumber)

        call.respond(
            HttpStatusCode.OK,
            ManualPullResponse(
                newCount = result?.newCount ?: 0,
                skippedCount = result?.skippedCount ?: 0,
                fetchCycles = result?.fetchCycles ?: 0,
                cursorAdvanced = result?.cursorAdvanced ?: false,
                triggeredAtUtc = triggeredAt,
                pumpMatchCount = result?.pumpMatchCount,
            )
        )
    }
}

package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import io.ktor.http.HttpStatusCode
import io.ktor.server.request.receive
import io.ktor.server.response.respond
import io.ktor.server.routing.Routing
import io.ktor.server.routing.get
import io.ktor.server.routing.post
import java.time.Instant
import java.util.UUID

/**
 * Transaction endpoints — locally buffered transaction access.
 * All routes use /api/v1/ prefix per edge-agent-local-api.yaml.
 *
 * GET  /api/v1/transactions             — list buffered transactions (p95 <= 150 ms at 30k records)
 * GET  /api/v1/transactions/{id}        — get single buffered transaction by middleware UUID
 * POST /api/v1/transactions/acknowledge — Odoo POS marks a batch of transactions as consumed
 *
 * Never depends on live FCC access. Excludes SYNCED_TO_ODOO records per §5.3 of
 * the state machine spec to prevent Odoo double-consumption.
 */
fun Routing.transactionRoutes(dao: TransactionBufferDao) {

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
        val pumpNumber = call.request.queryParameters["pumpNumber"]?.toIntOrNull()
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

        val total = dao.countForLocalApi()

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

        val found = request.transactionIds.count { id -> dao.getById(id) != null }
        call.respond(HttpStatusCode.OK, BatchAcknowledgeResponse(acknowledged = found))
    }
}

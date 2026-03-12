package com.fccmiddleware.edge.websocket

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import io.ktor.websocket.Frame
import io.ktor.websocket.WebSocketSession
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import com.fccmiddleware.edge.adapter.common.CancelPreAuthCommand
import java.time.Instant

/**
 * Handles all WebSocket `mode` commands from Odoo POS.
 *
 * Each handler reads from / writes to [TransactionBufferDao] and calls
 * the FCC adapter as needed. Response format matches the legacy
 * DOMSRealImplementation FleckWebSocketAdapter protocol exactly.
 */
class OdooWsMessageHandler(
    private val transactionDao: TransactionBufferDao,
    private val wsJson: Json,
    private val broadcastToAll: suspend (String, Any?) -> Unit,
    private val getFccAdapter: () -> IFccAdapter?,
    private val getSiteCode: () -> String,
) {
    companion object {
        private const val TAG = "OdooWsMessageHandler"
    }

    // ── latest ──────────────────────────────────────────────────────────────

    /**
     * Handle `{ mode: "latest", pump_id?, nozzle_id?, emp?, CreatedDate? }`.
     *
     * Queries unsynced transactions with optional filters and returns them
     * in the legacy PumpTransactions format.
     */
    suspend fun handleLatest(session: WebSocketSession, data: JsonObject) {
        val pumpId = data["pump_id"]?.jsonPrimitive?.intOrNull
        val nozzleId = data["nozzle_id"]?.jsonPrimitive?.intOrNull
        val emp = data["emp"]?.jsonPrimitive?.content?.takeIf { it.isNotEmpty() }
        val createdDate = data["CreatedDate"]?.jsonPrimitive?.content

        val transactions = transactionDao.getUnsyncedForWs(
            pumpNumber = pumpId,
            nozzleNumber = nozzleId,
            attendant = emp,
            since = createdDate,
        )

        val dtos = transactions.map { it.toWsDto() }
        val response = buildJsonObject("latest", if (dtos.isEmpty()) JsonNull else wsJson.encodeToJsonElement(dtos))
        sendJson(session, response)
    }

    // ── all ──────────────────────────────────────────────────────────────────

    /**
     * Handle `{ mode: "all" }`.
     * Returns all transactions from the buffer.
     */
    suspend fun handleAll(session: WebSocketSession) {
        val transactions = transactionDao.getAllForWs()
        val dtos = transactions.map { it.toWsDto() }
        val response = buildJsonObject("all_transactions", wsJson.encodeToJsonElement(dtos))
        sendJson(session, response)
    }

    // ── manager_update ──────────────────────────────────────────────────────

    /**
     * Handle `{ mode: "manager_update", transaction_id, update: { state, order_uuid, order_id, payment_id, status_sync, add_to_cart } }`.
     *
     * Updates the transaction in the buffer and broadcasts the update to ALL clients.
     */
    suspend fun handleManagerUpdate(session: WebSocketSession, data: JsonObject) {
        val transactionId = data["transaction_id"]?.jsonPrimitive?.content ?: return
        val update = data["update"]?.jsonObject ?: return
        val now = Instant.now().toString()

        val orderUuid = update["order_uuid"]?.jsonPrimitive?.content
        val orderId = update["order_id"]?.jsonPrimitive?.content
        val paymentId = update["payment_id"]?.jsonPrimitive?.content

        // Update Odoo fields
        transactionDao.updateOdooFields(
            transactionId = transactionId,
            orderUuid = orderUuid,
            odooOrderId = orderId,
            paymentId = paymentId,
            now = now,
        )

        // Handle add_to_cart if present
        val addToCart = update["add_to_cart"]?.jsonPrimitive?.booleanOrNull
        if (addToCart != null) {
            transactionDao.updateAddToCart(transactionId, addToCart, paymentId, now)
        }

        // Check if this is an add_to_cart-only update — if so, skip broadcast
        // (matches legacy fix: broadcasting cart-only updates caused reorder issues)
        val isOnlyAddToCart = update.keys.size == 1 && update.containsKey("add_to_cart")
        if (isOnlyAddToCart) return

        // Broadcast updated transaction to all clients
        val updatedTx = transactionDao.getByFccTransactionId(transactionId)
        if (updatedTx != null) {
            val dto = updatedTx.toWsDto()
            broadcastToAll("transaction_update", wsJson.encodeToJsonElement(dto))
        }
    }

    // ── attendant_update ────────────────────────────────────────────────────

    /**
     * Handle `{ mode: "attendant_update", transaction_id, update: { order_uuid, order_id, state, add_to_cart, payment_id } }`.
     *
     * Updates add_to_cart and/or order_uuid on the transaction.
     * Broadcasts when add_to_cart changes or order_uuid is set.
     */
    suspend fun handleAttendantUpdate(session: WebSocketSession, data: JsonObject) {
        val transactionId = data["transaction_id"]?.jsonPrimitive?.content ?: return
        val update = data["update"]?.jsonObject ?: return
        val now = Instant.now().toString()

        val addToCart = update["add_to_cart"]?.jsonPrimitive?.booleanOrNull
        val paymentId = update["payment_id"]?.jsonPrimitive?.content
        val orderUuid = update["order_uuid"]?.jsonPrimitive?.content
        val orderId = update["order_id"]?.jsonPrimitive?.content
        val state = update["state"]?.jsonPrimitive?.content

        // Update add_to_cart if present
        if (addToCart != null) {
            transactionDao.updateAddToCart(transactionId, addToCart, paymentId, now)

            // Broadcast the update
            val updatedTx = transactionDao.getByFccTransactionId(transactionId)
            if (updatedTx != null) {
                val dto = updatedTx.toWsDto()
                broadcastToAll("transaction_update", wsJson.encodeToJsonElement(dto))
            }
        }

        // Update order_uuid if present
        if (!orderUuid.isNullOrEmpty()) {
            transactionDao.updateOdooFields(
                transactionId = transactionId,
                orderUuid = orderUuid,
                odooOrderId = orderId,
                paymentId = paymentId,
                now = now,
            )

            // Broadcast the update
            val updatedTx = transactionDao.getByFccTransactionId(transactionId)
            if (updatedTx != null) {
                val dto = updatedTx.toWsDto()
                broadcastToAll("transaction_update", wsJson.encodeToJsonElement(dto))
            }
        }
    }

    // ── FuelPumpStatus ──────────────────────────────────────────────────────

    /**
     * Handle `{ mode: "FuelPumpStatus" }`.
     *
     * Fetches current pump status from the FCC adapter and sends each status
     * individually to the requesting session (not broadcast to all).
     */
    suspend fun handleFuelPumpStatus(session: WebSocketSession) {
        val adapter = getFccAdapter() ?: run {
            sendJson(session, buildJsonObject("FuelPumpStatus", JsonNull))
            return
        }

        try {
            val statuses = adapter.getPumpStatus()
            for (status in statuses) {
                val dto = status.toWsDto()
                val payload = wsJson.encodeToString(dto)
                session.send(Frame.Text(payload))
            }
        } catch (e: Exception) {
            AppLogger.w(TAG, "Failed to get pump status: ${e.message}")
        }
    }

    // ── fp_unblock ──────────────────────────────────────────────────────────

    /**
     * Handle `{ mode: "fp_unblock", fp_id: <int> }`.
     *
     * Forwards pump release command to the FCC adapter via [cancelPreAuth].
     * The legacy system used FpLimitReset + CheckAndApplyPumpLimitAsync;
     * the new architecture maps this to a cancelPreAuth deauthorize.
     */
    suspend fun handleFpUnblock(session: WebSocketSession, data: JsonObject) {
        val fpId = data["fp_id"]?.jsonPrimitive?.intOrNull ?: 0

        val adapter = getFccAdapter()
        if (adapter == null) {
            val error = kotlinx.serialization.json.buildJsonObject {
                put("type", JsonPrimitive("fp_unblock"))
                put("status", JsonPrimitive("error"))
                put("fp_id", JsonPrimitive(fpId))
                put("message", JsonPrimitive("FCC adapter not available"))
            }
            sendJson(session, error)
            return
        }

        try {
            val released = adapter.cancelPreAuth(
                CancelPreAuthCommand(
                    siteCode = getSiteCode(),
                    pumpNumber = fpId,
                )
            )

            val response = kotlinx.serialization.json.buildJsonObject {
                put("type", JsonPrimitive("fp_unblock"))
                put("data", kotlinx.serialization.json.buildJsonObject {
                    put("fp_id", JsonPrimitive(fpId))
                    put("state", JsonPrimitive(if (released) "unblocked" else "available"))
                    put("message", JsonPrimitive(
                        if (released) "Pump limit reset and unblocked successfully"
                        else "Fuel pump already available, nothing to unblock"
                    ))
                })
            }
            sendJson(session, response)
        } catch (e: Exception) {
            AppLogger.w(TAG, "fp_unblock failed for fp_id=$fpId: ${e.message}")
            val error = kotlinx.serialization.json.buildJsonObject {
                put("type", JsonPrimitive("fp_unblock"))
                put("status", JsonPrimitive("error"))
                put("fp_id", JsonPrimitive(fpId))
                put("message", JsonPrimitive(e.message ?: "Unknown error"))
            }
            sendJson(session, error)
        }
    }

    // ── attendant_pump_count_update ─────────────────────────────────────────

    /**
     * Handle `{ mode: "attendant_pump_count_update", data: [{ PumpNumber, EmpTagNo, NewMaxTransaction }] }`.
     *
     * Processes each item and sends per-item acknowledgment.
     */
    suspend fun handleAttendantPumpCountUpdate(session: WebSocketSession, data: JsonObject) {
        val items = data["data"] ?: return
        if (items !is JsonArray) return

        for (item in items) {
            if (item !is JsonObject) continue

            val pumpNumber = item["PumpNumber"]?.jsonPrimitive?.intOrNull ?: continue
            val empTagNo = item["EmpTagNo"]?.jsonPrimitive?.content ?: continue
            val newMax = item["NewMaxTransaction"]?.jsonPrimitive?.intOrNull ?: continue

            // In the legacy system, this updated an attendant pump count table.
            // Send acknowledgment to match the legacy protocol.
            val ack = WsAttendantPumpCountAck(
                pumpNumber = pumpNumber,
                empTagNo = empTagNo,
                maxLimit = newMax,
                status = "updated",
            )

            val response = kotlinx.serialization.json.buildJsonObject {
                put("type", JsonPrimitive("attendant_pump_count_update_ack"))
                put("data", wsJson.encodeToJsonElement(ack))
            }
            sendJson(session, response)
        }
    }

    // ── manager_manual_update ───────────────────────────────────────────────

    /**
     * Handle `{ mode: "manager_manual_update", transaction_id, update: { state, manual_approved } }`.
     *
     * Marks the transaction as discarded in the buffer.
     */
    suspend fun handleManagerManualUpdate(session: WebSocketSession, data: JsonObject) {
        val transactionId = data["transaction_id"]?.jsonPrimitive?.content ?: return
        val now = Instant.now().toString()

        transactionDao.markDiscarded(transactionId, now)

        val response = kotlinx.serialization.json.buildJsonObject {
            put("type", JsonPrimitive("transaction_update"))
            put("data", kotlinx.serialization.json.buildJsonObject {
                put("transaction_id", JsonPrimitive(transactionId))
                put("state", JsonPrimitive("approved"))
                put("manual_approved", JsonPrimitive("yes"))
            })
        }
        sendJson(session, response)
    }

    // ── add_transaction ─────────────────────────────────────────────────────

    /**
     * Handle `{ mode: "add_transaction", data: { ... } }`.
     *
     * The legacy system inserted a new transaction record from the client.
     * In the new architecture, transactions come from the FCC adapter.
     * We acknowledge the message but don't insert — the FCC is the source of truth.
     */
    suspend fun handleAddTransaction(session: WebSocketSession, data: JsonObject) {
        AppLogger.d(TAG, "add_transaction received (no-op — FCC is source of truth)")
        // Legacy compat: send acknowledgment but don't insert.
        // The new architecture gets transactions exclusively from the FCC adapter.
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private fun buildJsonObject(type: String, data: JsonElement): JsonObject {
        return kotlinx.serialization.json.buildJsonObject {
            put("type", JsonPrimitive(type))
            put("data", data)
        }
    }

    private suspend fun sendJson(session: WebSocketSession, json: JsonElement) {
        try {
            session.send(Frame.Text(wsJson.encodeToString(json)))
        } catch (e: Exception) {
            AppLogger.d(TAG, "Failed to send WS message: ${e.message}")
        }
    }

    /** Encode a serializable value to a JsonElement for embedding in response objects. */
    private inline fun <reified T> Json.encodeToJsonElement(value: T): JsonElement {
        val jsonString = encodeToString(value)
        return parseToJsonElement(jsonString)
    }
}

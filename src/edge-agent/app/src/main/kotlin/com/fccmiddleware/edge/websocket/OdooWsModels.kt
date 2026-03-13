package com.fccmiddleware.edge.websocket

import com.fccmiddleware.edge.buffer.dao.WsBufferedTransaction
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.adapter.common.CurrencyUtils
import com.fccmiddleware.edge.adapter.common.PumpStatus
import com.fccmiddleware.edge.adapter.common.PumpState
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

// ---------------------------------------------------------------------------
// Outbound DTOs — sent TO Odoo POS over WebSocket
// ---------------------------------------------------------------------------

/**
 * Transaction DTO matching the legacy DOMSRealImplementation PumpTransactions model.
 *
 * JSON field names MUST match the legacy contract exactly — Odoo POS parses these
 * by name and any deviation breaks backward compatibility.
 */
@Serializable
data class PumpTransactionWsDto(
    @SerialName("id") val id: Int,
    @SerialName("transaction_id") val transactionId: String,
    @SerialName("pump_id") val pumpId: Int,
    @SerialName("nozzle_id") val nozzleId: Int,
    @SerialName("attendant") val attendant: String?,
    @SerialName("product_id") val productId: String,
    @SerialName("qty") val qty: Double,
    @SerialName("unit_price") val unitPrice: Double,
    @SerialName("total") val total: Double,
    @SerialName("state") val state: String,
    @SerialName("start_time") val startTime: String,
    @SerialName("end_time") val endTime: String,
    @SerialName("order_uuid") val orderUuid: String?,
    @SerialName("sync_status") val syncStatus: Int,
    @SerialName("odoo_order_id") val odooOrderId: String?,
    @SerialName("add_to_cart") val addToCart: Boolean,
    @SerialName("payment_id") val paymentId: String?,
)

/**
 * Fuel pump status DTO matching the legacy FuelPumpStatusDto.
 *
 * WARNING: mixed casing is intentional — legacy contract uses:
 * - snake_case: pump_number, nozzle_number, unit_price
 * - PascalCase: FpGradeOptionNo
 * - camelCase: isOnline
 * Odoo POS reads these field names directly. Do NOT normalise casing.
 */
@Serializable
data class FuelPumpStatusWsDto(
    @SerialName("pump_number") val pumpNumber: Int,
    @SerialName("nozzle_number") val nozzleNumber: Int,
    @SerialName("status") val status: String,
    @SerialName("reading") val reading: Double,
    @SerialName("volume") val volume: Double,
    @SerialName("litre") val litre: Double,
    @SerialName("amount") val amount: Double,
    @SerialName("attendant") val attendant: String?,
    @SerialName("count") val count: Int,
    @SerialName("FpGradeOptionNo") val fpGradeOptionNo: Int,
    @SerialName("unit_price") val unitPrice: Double?,
    @SerialName("isOnline") val isOnline: Boolean,
)

/**
 * WebSocket error response.
 */
@Serializable
data class WsErrorResponse(
    val status: String = "error",
    val message: String,
)

/**
 * Attendant pump count update acknowledgment.
 */
@Serializable
data class WsAttendantPumpCountAck(
    @SerialName("pump_number") val pumpNumber: Int,
    @SerialName("emp_tag_no") val empTagNo: String,
    @SerialName("max_limit") val maxLimit: Int,
    @SerialName("status") val status: String,
)

// ---------------------------------------------------------------------------
// Inbound message — parsed from the `mode` field
// ---------------------------------------------------------------------------

/**
 * Inbound attendant pump count update item.
 */
@Serializable
data class AttendantPumpCountUpdateItem(
    @SerialName("PumpNumber") val pumpNumber: Int,
    @SerialName("EmpTagNo") val empTagNo: String,
    @SerialName("NewMaxTransaction") val newMaxTransaction: Int,
)

// ---------------------------------------------------------------------------
// Mapping helpers
// ---------------------------------------------------------------------------

/**
 * Auto-incrementing counter for legacy integer `id` field.
 * AF-048: Initialized from epoch seconds so IDs never collide with a previous session
 * after process restart. Monotonically increments within a session.
 */
private val txIdCounter = java.util.concurrent.atomic.AtomicInteger(
    (System.currentTimeMillis() / 1000).toInt()
)

/**
 * Map a [BufferedTransaction] to the legacy [PumpTransactionWsDto] format.
 *
 * Monetary conversion: minor units → major units (divide by currency factor).
 * Volume conversion: microlitres → litres (divide by 1,000,000).
 */
fun BufferedTransaction.toWsDto(): PumpTransactionWsDto {
    val qty = volumeMicrolitres / 1_000_000.0
    val currencyFactor = CurrencyUtils.getFactor(currencyCode)
    val price = unitPriceMinorPerLitre / currencyFactor
    val total = amountMinorUnits / currencyFactor

    val wsState = when {
        isDiscard -> "discard"
        syncStatus == "SYNCED_TO_ODOO" -> "approved"
        syncStatus == "UPLOADED" -> "approved"
        else -> "pending"
    }
    val wsSyncStatus = if (syncStatus == "PENDING") 0 else 1

    return PumpTransactionWsDto(
        id = txIdCounter.incrementAndGet(),
        transactionId = fccTransactionId,
        pumpId = pumpNumber,
        nozzleId = nozzleNumber,
        attendant = attendantId,
        productId = productCode,
        qty = qty,
        unitPrice = price,
        total = total,
        state = wsState,
        startTime = startedAt,
        endTime = completedAt,
        orderUuid = orderUuid,
        syncStatus = wsSyncStatus,
        odooOrderId = odooOrderId,
        addToCart = addToCart,
        paymentId = paymentId,
    )
}

/**
 * AP-025: Map a [WsBufferedTransaction] projection to the legacy [PumpTransactionWsDto] format.
 * Same logic as [BufferedTransaction.toWsDto] but operates on the lightweight projection
 * that excludes rawPayloadJson.
 */
fun WsBufferedTransaction.toWsDto(): PumpTransactionWsDto {
    val qty = volumeMicrolitres / 1_000_000.0
    val currencyFactor = CurrencyUtils.getFactor(currencyCode)
    val price = unitPriceMinorPerLitre / currencyFactor
    val total = amountMinorUnits / currencyFactor

    val wsState = when {
        isDiscard -> "discard"
        syncStatus == "SYNCED_TO_ODOO" -> "approved"
        syncStatus == "UPLOADED" -> "approved"
        else -> "pending"
    }
    val wsSyncStatus = if (syncStatus == "PENDING") 0 else 1

    return PumpTransactionWsDto(
        id = txIdCounter.incrementAndGet(),
        transactionId = fccTransactionId,
        pumpId = pumpNumber,
        nozzleId = nozzleNumber,
        attendant = attendantId,
        productId = productCode,
        qty = qty,
        unitPrice = price,
        total = total,
        state = wsState,
        startTime = startedAt,
        endTime = completedAt,
        orderUuid = orderUuid,
        syncStatus = wsSyncStatus,
        odooOrderId = odooOrderId,
        addToCart = addToCart,
        paymentId = paymentId,
    )
}

/**
 * Map a [PumpStatus] to the legacy [FuelPumpStatusWsDto] format.
 */
fun PumpStatus.toWsDto(): FuelPumpStatusWsDto {
    val volume = currentVolumeLitres?.toDoubleOrNull() ?: 0.0
    val amount = currentAmount?.toDoubleOrNull() ?: 0.0
    val price = unitPrice?.toDoubleOrNull()

    val statusStr = when (state) {
        PumpState.IDLE -> "idle"
        PumpState.AUTHORIZED -> "authorized"
        PumpState.CALLING -> "calling"
        PumpState.DISPENSING -> "dispensing"
        PumpState.PAUSED -> "suspended"
        PumpState.COMPLETED -> "idle"
        PumpState.ERROR -> "inoperative"
        PumpState.OFFLINE -> "offline"
        PumpState.UNKNOWN -> "unknown"
    }

    return FuelPumpStatusWsDto(
        pumpNumber = pumpNumber,
        nozzleNumber = nozzleNumber,
        status = statusStr,
        reading = 0.0,
        volume = volume,
        litre = volume,
        amount = amount,
        attendant = null,
        count = 0,
        fpGradeOptionNo = 0,
        unitPrice = price,
        isOnline = state != PumpState.OFFLINE && state != PumpState.ERROR,
    )
}

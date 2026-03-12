package com.fccmiddleware.edge.websocket

import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Serialization round-trip tests for WebSocket DTOs.
 *
 * Validates that:
 *   - Each DTO serializes to JSON with the exact legacy field names
 *   - Mixed casing (snake_case, PascalCase, camelCase) is preserved
 *   - Deserialization from JSON produces an equal object
 *   - Null fields are handled correctly
 */
class OdooWsModelsTest {

    private val json = Json {
        encodeDefaults = true
        ignoreUnknownKeys = true
    }

    // -------------------------------------------------------------------------
    // PumpTransactionWsDto
    // -------------------------------------------------------------------------

    @Test
    fun `PumpTransactionWsDto round-trip preserves all fields`() {
        val dto = PumpTransactionWsDto(
            id = 42,
            transactionId = "TXN-001",
            pumpId = 3,
            nozzleId = 1,
            attendant = "EMP-100",
            productId = "DIESEL",
            qty = 45.678,
            unitPrice = 1.25,
            total = 57.10,
            state = "pending",
            startTime = "2024-06-15T10:00:00Z",
            endTime = "2024-06-15T10:05:00Z",
            orderUuid = "uuid-abc-123",
            syncStatus = 0,
            odooOrderId = "SO-456",
            addToCart = true,
            paymentId = "PAY-789",
        )

        val encoded = json.encodeToString(dto)
        val decoded = json.decodeFromString<PumpTransactionWsDto>(encoded)

        assertEquals(dto, decoded)
    }

    @Test
    fun `PumpTransactionWsDto uses exact legacy field names`() {
        val dto = PumpTransactionWsDto(
            id = 1, transactionId = "T1", pumpId = 1, nozzleId = 1,
            attendant = null, productId = "P1", qty = 0.0, unitPrice = 0.0,
            total = 0.0, state = "pending", startTime = "", endTime = "",
            orderUuid = null, syncStatus = 0, odooOrderId = null,
            addToCart = false, paymentId = null,
        )

        val encoded = json.encodeToString(dto)
        val obj = json.parseToJsonElement(encoded).jsonObject

        val expectedKeys = setOf(
            "id", "transaction_id", "pump_id", "nozzle_id", "attendant",
            "product_id", "qty", "unit_price", "total", "state",
            "start_time", "end_time", "order_uuid", "sync_status",
            "odoo_order_id", "add_to_cart", "payment_id",
        )
        assertEquals(expectedKeys, obj.keys)
    }

    @Test
    fun `PumpTransactionWsDto handles null optional fields`() {
        val dto = PumpTransactionWsDto(
            id = 1, transactionId = "T1", pumpId = 1, nozzleId = 1,
            attendant = null, productId = "P1", qty = 0.0, unitPrice = 0.0,
            total = 0.0, state = "pending", startTime = "", endTime = "",
            orderUuid = null, syncStatus = 0, odooOrderId = null,
            addToCart = false, paymentId = null,
        )

        val encoded = json.encodeToString(dto)
        val decoded = json.decodeFromString<PumpTransactionWsDto>(encoded)

        assertNull(decoded.attendant)
        assertNull(decoded.orderUuid)
        assertNull(decoded.odooOrderId)
        assertNull(decoded.paymentId)
    }

    // -------------------------------------------------------------------------
    // FuelPumpStatusWsDto — mixed casing is critical
    // -------------------------------------------------------------------------

    @Test
    fun `FuelPumpStatusWsDto round-trip preserves all fields`() {
        val dto = FuelPumpStatusWsDto(
            pumpNumber = 2,
            nozzleNumber = 1,
            status = "dispensing",
            reading = 123.45,
            volume = 30.5,
            litre = 30.5,
            amount = 38.12,
            attendant = "EMP-200",
            count = 5,
            fpGradeOptionNo = 3,
            unitPrice = 1.25,
            isOnline = true,
        )

        val encoded = json.encodeToString(dto)
        val decoded = json.decodeFromString<FuelPumpStatusWsDto>(encoded)

        assertEquals(dto, decoded)
    }

    @Test
    fun `FuelPumpStatusWsDto preserves mixed-case field names`() {
        val dto = FuelPumpStatusWsDto(
            pumpNumber = 1, nozzleNumber = 1, status = "idle",
            reading = 0.0, volume = 0.0, litre = 0.0, amount = 0.0,
            attendant = null, count = 0, fpGradeOptionNo = 0,
            unitPrice = null, isOnline = true,
        )

        val encoded = json.encodeToString(dto)
        val obj = json.parseToJsonElement(encoded).jsonObject

        // snake_case fields
        assertTrue("Expected snake_case pump_number", obj.containsKey("pump_number"))
        assertTrue("Expected snake_case nozzle_number", obj.containsKey("nozzle_number"))
        assertTrue("Expected snake_case unit_price", obj.containsKey("unit_price"))

        // PascalCase field — legacy DOMS contract
        assertTrue("Expected PascalCase FpGradeOptionNo", obj.containsKey("FpGradeOptionNo"))

        // camelCase field — legacy DOMS contract
        assertTrue("Expected camelCase isOnline", obj.containsKey("isOnline"))

        // Verify no normalized versions exist
        assertFalse("Should NOT have fp_grade_option_no", obj.containsKey("fp_grade_option_no"))
        assertFalse("Should NOT have is_online", obj.containsKey("is_online"))
    }

    @Test
    fun `FuelPumpStatusWsDto handles null unit_price and attendant`() {
        val dto = FuelPumpStatusWsDto(
            pumpNumber = 1, nozzleNumber = 1, status = "idle",
            reading = 0.0, volume = 0.0, litre = 0.0, amount = 0.0,
            attendant = null, count = 0, fpGradeOptionNo = 0,
            unitPrice = null, isOnline = false,
        )

        val encoded = json.encodeToString(dto)
        val decoded = json.decodeFromString<FuelPumpStatusWsDto>(encoded)

        assertNull(decoded.unitPrice)
        assertNull(decoded.attendant)
        assertFalse(decoded.isOnline)
    }

    // -------------------------------------------------------------------------
    // WsErrorResponse
    // -------------------------------------------------------------------------

    @Test
    fun `WsErrorResponse round-trip preserves fields`() {
        val dto = WsErrorResponse(message = "Unknown mode 'test'")

        val encoded = json.encodeToString(dto)
        val decoded = json.decodeFromString<WsErrorResponse>(encoded)

        assertEquals(dto, decoded)
        assertEquals("error", decoded.status)
        assertEquals("Unknown mode 'test'", decoded.message)
    }

    @Test
    fun `WsErrorResponse defaults status to error`() {
        val dto = WsErrorResponse(message = "fail")

        val encoded = json.encodeToString(dto)

        assertTrue(encoded.contains("\"status\":\"error\""))
    }

    // -------------------------------------------------------------------------
    // WsAttendantPumpCountAck
    // -------------------------------------------------------------------------

    @Test
    fun `WsAttendantPumpCountAck round-trip preserves fields`() {
        val dto = WsAttendantPumpCountAck(
            pumpNumber = 4,
            empTagNo = "EMP-300",
            maxLimit = 10,
            status = "updated",
        )

        val encoded = json.encodeToString(dto)
        val decoded = json.decodeFromString<WsAttendantPumpCountAck>(encoded)

        assertEquals(dto, decoded)
    }

    @Test
    fun `WsAttendantPumpCountAck uses snake_case field names`() {
        val dto = WsAttendantPumpCountAck(
            pumpNumber = 1, empTagNo = "E1", maxLimit = 5, status = "updated",
        )

        val encoded = json.encodeToString(dto)
        val obj = json.parseToJsonElement(encoded).jsonObject

        val expectedKeys = setOf("pump_number", "emp_tag_no", "max_limit", "status")
        assertEquals(expectedKeys, obj.keys)
    }

    // -------------------------------------------------------------------------
    // AttendantPumpCountUpdateItem — inbound PascalCase
    // -------------------------------------------------------------------------

    @Test
    fun `AttendantPumpCountUpdateItem round-trip preserves fields`() {
        val dto = AttendantPumpCountUpdateItem(
            pumpNumber = 2,
            empTagNo = "EMP-400",
            newMaxTransaction = 15,
        )

        val encoded = json.encodeToString(dto)
        val decoded = json.decodeFromString<AttendantPumpCountUpdateItem>(encoded)

        assertEquals(dto, decoded)
    }

    @Test
    fun `AttendantPumpCountUpdateItem uses PascalCase field names`() {
        val dto = AttendantPumpCountUpdateItem(
            pumpNumber = 1, empTagNo = "E1", newMaxTransaction = 5,
        )

        val encoded = json.encodeToString(dto)
        val obj = json.parseToJsonElement(encoded).jsonObject

        val expectedKeys = setOf("PumpNumber", "EmpTagNo", "NewMaxTransaction")
        assertEquals(expectedKeys, obj.keys)
    }

    @Test
    fun `AttendantPumpCountUpdateItem deserializes from Odoo POS payload`() {
        // Simulate the exact JSON Odoo POS sends
        val incomingJson = """{"PumpNumber": 3, "EmpTagNo": "ATT-99", "NewMaxTransaction": 20}"""

        val decoded = json.decodeFromString<AttendantPumpCountUpdateItem>(incomingJson)

        assertEquals(3, decoded.pumpNumber)
        assertEquals("ATT-99", decoded.empTagNo)
        assertEquals(20, decoded.newMaxTransaction)
    }

    // -------------------------------------------------------------------------
    // Cross-DTO: verify legacy Odoo POS compatibility
    // -------------------------------------------------------------------------

    @Test
    fun `FuelPumpStatusWsDto can be decoded from legacy DOMS JSON`() {
        // Simulate the exact JSON the legacy DOMS system would emit
        val legacyJson = """
        {
            "pump_number": 5,
            "nozzle_number": 2,
            "status": "dispensing",
            "reading": 456.78,
            "volume": 25.0,
            "litre": 25.0,
            "amount": 31.25,
            "attendant": "EMP-500",
            "count": 12,
            "FpGradeOptionNo": 1,
            "unit_price": 1.25,
            "isOnline": true
        }
        """.trimIndent()

        val decoded = json.decodeFromString<FuelPumpStatusWsDto>(legacyJson)

        assertEquals(5, decoded.pumpNumber)
        assertEquals(2, decoded.nozzleNumber)
        assertEquals("dispensing", decoded.status)
        assertEquals(1, decoded.fpGradeOptionNo)
        assertEquals(1.25, decoded.unitPrice)
        assertTrue(decoded.isOnline)
    }

    @Test
    fun `PumpTransactionWsDto can be decoded from legacy DOMS JSON`() {
        val legacyJson = """
        {
            "id": 99,
            "transaction_id": "TXN-LEGACY",
            "pump_id": 2,
            "nozzle_id": 1,
            "attendant": "ATT-1",
            "product_id": "ULP95",
            "qty": 40.0,
            "unit_price": 1.50,
            "total": 60.0,
            "state": "approved",
            "start_time": "2024-01-01T08:00:00Z",
            "end_time": "2024-01-01T08:03:00Z",
            "order_uuid": "uuid-legacy",
            "sync_status": 1,
            "odoo_order_id": "SO-100",
            "add_to_cart": true,
            "payment_id": "PAY-100"
        }
        """.trimIndent()

        val decoded = json.decodeFromString<PumpTransactionWsDto>(legacyJson)

        assertEquals(99, decoded.id)
        assertEquals("TXN-LEGACY", decoded.transactionId)
        assertEquals(2, decoded.pumpId)
        assertEquals("ULP95", decoded.productId)
        assertEquals(40.0, decoded.qty, 0.001)
        assertEquals("approved", decoded.state)
        assertTrue(decoded.addToCart)
        assertEquals("PAY-100", decoded.paymentId)
    }
}

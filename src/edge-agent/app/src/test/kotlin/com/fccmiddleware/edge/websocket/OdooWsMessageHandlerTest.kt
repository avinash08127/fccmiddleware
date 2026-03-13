package com.fccmiddleware.edge.websocket

import com.fccmiddleware.edge.adapter.common.CancelPreAuthCommand
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.adapter.common.PumpState
import com.fccmiddleware.edge.adapter.common.PumpStatus
import com.fccmiddleware.edge.adapter.common.PumpStatusSource
import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import io.ktor.websocket.Frame
import io.ktor.websocket.WebSocketSession
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import io.mockk.slot
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * AT-046: Unit tests for [OdooWsMessageHandler] business logic.
 *
 * Covers message routing, DAO interactions, broadcast logic,
 * adapter calls, and error handling that were previously untested.
 */
class OdooWsMessageHandlerTest {

    private val bufferManager = mockk<TransactionBufferManager>()
    private val fccAdapter = mockk<IFccAdapter>()
    private val wsJson = Json {
        encodeDefaults = true
        ignoreUnknownKeys = true
        isLenient = true
    }

    private val sentFrames = mutableListOf<String>()
    private val broadcasts = mutableListOf<Pair<String, Any?>>()

    private lateinit var handler: OdooWsMessageHandler
    private val session = mockk<WebSocketSession>()

    @Before
    fun setUp() {
        sentFrames.clear()
        broadcasts.clear()

        coEvery { session.send(any<Frame>()) } answers {
            val frame = firstArg<Frame>()
            if (frame is Frame.Text) {
                sentFrames.add(frame.readText())
            }
        }

        handler = OdooWsMessageHandler(
            bufferManager = bufferManager,
            wsJson = wsJson,
            broadcastToAll = { type, data -> broadcasts.add(type to data) },
            getFccAdapter = { fccAdapter },
            getSiteCode = { "SITE001" },
        )
    }

    // -------------------------------------------------------------------------
    // handleLatest
    // -------------------------------------------------------------------------

    @Test
    fun `handleLatest returns empty list when no unsynced transactions`() = runTest {
        coEvery { bufferManager.getUnsyncedForWs(any(), any(), any(), any()) } returns emptyList()

        val data = buildJsonObject { put("mode", JsonPrimitive("latest")) }
        handler.handleLatest(session, data)

        assertEquals(1, sentFrames.size)
        val response = wsJson.parseToJsonElement(sentFrames[0]).jsonObject
        assertEquals("latest", response["type"]?.jsonPrimitive?.content)
    }

    @Test
    fun `handleLatest passes filter parameters to bufferManager`() = runTest {
        coEvery { bufferManager.getUnsyncedForWs(any(), any(), any(), any()) } returns emptyList()

        val data = buildJsonObject {
            put("mode", JsonPrimitive("latest"))
            put("pump_id", JsonPrimitive(3))
            put("nozzle_id", JsonPrimitive(1))
            put("emp", JsonPrimitive("EMP-100"))
            put("CreatedDate", JsonPrimitive("2024-01-01"))
        }
        handler.handleLatest(session, data)

        coVerify {
            bufferManager.getUnsyncedForWs(
                pumpNumber = 3,
                nozzleNumber = 1,
                attendant = "EMP-100",
                since = "2024-01-01",
            )
        }
    }

    // -------------------------------------------------------------------------
    // handleAll
    // -------------------------------------------------------------------------

    @Test
    fun `handleAll queries all transactions and sends response`() = runTest {
        coEvery { bufferManager.getAllForWs() } returns emptyList()

        handler.handleAll(session)

        assertEquals(1, sentFrames.size)
        val response = wsJson.parseToJsonElement(sentFrames[0]).jsonObject
        assertEquals("all_transactions", response["type"]?.jsonPrimitive?.content)
        coVerify { bufferManager.getAllForWs() }
    }

    // -------------------------------------------------------------------------
    // handleManagerUpdate
    // -------------------------------------------------------------------------

    @Test
    fun `handleManagerUpdate updates Odoo fields and broadcasts`() = runTest {
        val tx = stubTransaction()
        coEvery { bufferManager.getByFccTransactionId("TXN-001") } returns tx
        coEvery { bufferManager.updateOdooFields(any(), any(), any(), any(), any()) } returns Unit

        val data = buildJsonObject {
            put("mode", JsonPrimitive("manager_update"))
            put("transaction_id", JsonPrimitive("TXN-001"))
            put("update", buildJsonObject {
                put("order_uuid", JsonPrimitive("uuid-123"))
                put("order_id", JsonPrimitive("SO-100"))
                put("payment_id", JsonPrimitive("PAY-200"))
            })
        }
        handler.handleManagerUpdate(session, data)

        coVerify {
            bufferManager.updateOdooFields(
                transactionId = "TXN-001",
                orderUuid = "uuid-123",
                odooOrderId = "SO-100",
                paymentId = "PAY-200",
                now = any(),
            )
        }
        assertEquals(1, broadcasts.size)
        assertEquals("transaction_update", broadcasts[0].first)
    }

    @Test
    fun `handleManagerUpdate skips broadcast for add_to_cart-only update`() = runTest {
        val tx = stubTransaction()
        coEvery { bufferManager.getByFccTransactionId("TXN-001") } returns tx
        coEvery { bufferManager.updateAddToCart(any(), any(), any(), any()) } returns Unit

        val data = buildJsonObject {
            put("mode", JsonPrimitive("manager_update"))
            put("transaction_id", JsonPrimitive("TXN-001"))
            put("update", buildJsonObject {
                put("add_to_cart", JsonPrimitive(true))
            })
        }
        handler.handleManagerUpdate(session, data)

        coVerify { bufferManager.updateAddToCart("TXN-001", true, null, any()) }
        assertTrue("No broadcast for add_to_cart-only update", broadcasts.isEmpty())
    }

    @Test
    fun `handleManagerUpdate sends error when transaction not found`() = runTest {
        coEvery { bufferManager.getByFccTransactionId("MISSING") } returns null

        val data = buildJsonObject {
            put("mode", JsonPrimitive("manager_update"))
            put("transaction_id", JsonPrimitive("MISSING"))
            put("update", buildJsonObject {
                put("order_id", JsonPrimitive("SO-100"))
            })
        }
        handler.handleManagerUpdate(session, data)

        assertEquals(1, sentFrames.size)
        val response = wsJson.parseToJsonElement(sentFrames[0]).jsonObject
        assertEquals("error", response["type"]?.jsonPrimitive?.content)
        assertTrue(broadcasts.isEmpty())
    }

    @Test
    fun `handleManagerUpdate returns early when transaction_id missing`() = runTest {
        val data = buildJsonObject {
            put("mode", JsonPrimitive("manager_update"))
            put("update", buildJsonObject {
                put("order_id", JsonPrimitive("SO-100"))
            })
        }
        handler.handleManagerUpdate(session, data)

        assertTrue(sentFrames.isEmpty())
        assertTrue(broadcasts.isEmpty())
    }

    // -------------------------------------------------------------------------
    // handleAttendantUpdate — AF-042 single broadcast regression
    // -------------------------------------------------------------------------

    @Test
    fun `handleAttendantUpdate sends single broadcast when both add_to_cart and order_uuid present`() = runTest {
        val tx = stubTransaction()
        coEvery { bufferManager.getByFccTransactionId("TXN-001") } returns tx
        coEvery { bufferManager.updateAddToCart(any(), any(), any(), any()) } returns Unit
        coEvery { bufferManager.updateOdooFields(any(), any(), any(), any(), any()) } returns Unit

        val data = buildJsonObject {
            put("mode", JsonPrimitive("attendant_update"))
            put("transaction_id", JsonPrimitive("TXN-001"))
            put("update", buildJsonObject {
                put("add_to_cart", JsonPrimitive(true))
                put("order_uuid", JsonPrimitive("uuid-456"))
                put("payment_id", JsonPrimitive("PAY-300"))
            })
        }
        handler.handleAttendantUpdate(session, data)

        // AF-042: Both mutations should result in exactly ONE broadcast
        assertEquals(1, broadcasts.size)
        assertEquals("transaction_update", broadcasts[0].first)
        coVerify(exactly = 1) { bufferManager.updateAddToCart(any(), any(), any(), any()) }
        coVerify(exactly = 1) { bufferManager.updateOdooFields(any(), any(), any(), any(), any()) }
    }

    @Test
    fun `handleAttendantUpdate does not broadcast when no changes made`() = runTest {
        val tx = stubTransaction()
        coEvery { bufferManager.getByFccTransactionId("TXN-001") } returns tx

        val data = buildJsonObject {
            put("mode", JsonPrimitive("attendant_update"))
            put("transaction_id", JsonPrimitive("TXN-001"))
            put("update", buildJsonObject {
                // No add_to_cart, no order_uuid — nothing to change
                put("state", JsonPrimitive("pending"))
            })
        }
        handler.handleAttendantUpdate(session, data)

        assertTrue("No broadcast when nothing changed", broadcasts.isEmpty())
    }

    @Test
    fun `handleAttendantUpdate sends error when transaction not found`() = runTest {
        coEvery { bufferManager.getByFccTransactionId("MISSING") } returns null

        val data = buildJsonObject {
            put("mode", JsonPrimitive("attendant_update"))
            put("transaction_id", JsonPrimitive("MISSING"))
            put("update", buildJsonObject {
                put("add_to_cart", JsonPrimitive(true))
            })
        }
        handler.handleAttendantUpdate(session, data)

        assertEquals(1, sentFrames.size)
        val response = wsJson.parseToJsonElement(sentFrames[0]).jsonObject
        assertEquals("error", response["type"]?.jsonPrimitive?.content)
    }

    // -------------------------------------------------------------------------
    // handleFpUnblock
    // -------------------------------------------------------------------------

    @Test
    fun `handleFpUnblock sends success response when adapter releases pump`() = runTest {
        coEvery { fccAdapter.cancelPreAuth(any()) } returns true

        val data = buildJsonObject {
            put("mode", JsonPrimitive("fp_unblock"))
            put("fp_id", JsonPrimitive(3))
        }
        handler.handleFpUnblock(session, data)

        assertEquals(1, sentFrames.size)
        val response = wsJson.parseToJsonElement(sentFrames[0]).jsonObject
        assertEquals("fp_unblock", response["type"]?.jsonPrimitive?.content)
        val responseData = response["data"]?.jsonObject
        assertNotNull(responseData)
        assertEquals("unblocked", responseData!!["state"]?.jsonPrimitive?.content)
        assertEquals(3, responseData["fp_id"]?.jsonPrimitive?.content?.toInt())

        val cmdSlot = slot<CancelPreAuthCommand>()
        coVerify { fccAdapter.cancelPreAuth(capture(cmdSlot)) }
        assertEquals("SITE001", cmdSlot.captured.siteCode)
        assertEquals(3, cmdSlot.captured.pumpNumber)
    }

    @Test
    fun `handleFpUnblock sends available state when pump already released`() = runTest {
        coEvery { fccAdapter.cancelPreAuth(any()) } returns false

        val data = buildJsonObject {
            put("mode", JsonPrimitive("fp_unblock"))
            put("fp_id", JsonPrimitive(5))
        }
        handler.handleFpUnblock(session, data)

        assertEquals(1, sentFrames.size)
        val response = wsJson.parseToJsonElement(sentFrames[0]).jsonObject
        val responseData = response["data"]?.jsonObject
        assertEquals("available", responseData!!["state"]?.jsonPrimitive?.content)
    }

    @Test
    fun `handleFpUnblock sends error when adapter not available`() = runTest {
        val handlerNoAdapter = OdooWsMessageHandler(
            bufferManager = bufferManager,
            wsJson = wsJson,
            broadcastToAll = { _, _ -> },
            getFccAdapter = { null },
            getSiteCode = { "SITE001" },
        )

        val data = buildJsonObject {
            put("mode", JsonPrimitive("fp_unblock"))
            put("fp_id", JsonPrimitive(1))
        }
        handlerNoAdapter.handleFpUnblock(session, data)

        assertEquals(1, sentFrames.size)
        val response = wsJson.parseToJsonElement(sentFrames[0]).jsonObject
        assertEquals("error", response["status"]?.jsonPrimitive?.content)
    }

    @Test
    fun `handleFpUnblock sends error when adapter throws exception`() = runTest {
        coEvery { fccAdapter.cancelPreAuth(any()) } throws RuntimeException("Connection timeout")

        val data = buildJsonObject {
            put("mode", JsonPrimitive("fp_unblock"))
            put("fp_id", JsonPrimitive(2))
        }
        handler.handleFpUnblock(session, data)

        assertEquals(1, sentFrames.size)
        val response = wsJson.parseToJsonElement(sentFrames[0]).jsonObject
        assertEquals("error", response["status"]?.jsonPrimitive?.content)
        assertEquals("Failed to unblock pump 2", response["message"]?.jsonPrimitive?.content)
    }

    // -------------------------------------------------------------------------
    // handleAddTransaction — AF-043
    // -------------------------------------------------------------------------

    @Test
    fun `handleAddTransaction sends ack response`() = runTest {
        val data = buildJsonObject {
            put("mode", JsonPrimitive("add_transaction"))
            put("data", buildJsonObject {
                put("pump_id", JsonPrimitive(1))
            })
        }
        handler.handleAddTransaction(session, data)

        assertEquals(1, sentFrames.size)
        val response = wsJson.parseToJsonElement(sentFrames[0]).jsonObject
        assertEquals("add_transaction_ack", response["type"]?.jsonPrimitive?.content)
        assertEquals("ok", response["data"]?.jsonPrimitive?.content)
    }

    // -------------------------------------------------------------------------
    // handleManagerManualUpdate — AF-047
    // -------------------------------------------------------------------------

    @Test
    fun `handleManagerManualUpdate marks discarded and broadcasts to all clients`() = runTest {
        val tx = stubTransaction(isDiscard = true)
        coEvery { bufferManager.markDiscarded(any(), any()) } returns Unit
        coEvery { bufferManager.getByFccTransactionId("TXN-001") } returns tx

        val data = buildJsonObject {
            put("mode", JsonPrimitive("manager_manual_update"))
            put("transaction_id", JsonPrimitive("TXN-001"))
            put("update", buildJsonObject {
                put("state", JsonPrimitive("approved"))
                put("manual_approved", JsonPrimitive("yes"))
            })
        }
        handler.handleManagerManualUpdate(session, data)

        coVerify { bufferManager.markDiscarded("TXN-001", any()) }
        assertEquals(1, broadcasts.size)
        assertEquals("transaction_update", broadcasts[0].first)
    }

    @Test
    fun `handleManagerManualUpdate sends response to session when transaction archived`() = runTest {
        coEvery { bufferManager.markDiscarded(any(), any()) } returns Unit
        coEvery { bufferManager.getByFccTransactionId("ARCHIVED") } returns null

        val data = buildJsonObject {
            put("mode", JsonPrimitive("manager_manual_update"))
            put("transaction_id", JsonPrimitive("ARCHIVED"))
        }
        handler.handleManagerManualUpdate(session, data)

        assertTrue("No broadcast when transaction not found", broadcasts.isEmpty())
        assertEquals(1, sentFrames.size)
        val response = wsJson.parseToJsonElement(sentFrames[0]).jsonObject
        assertEquals("transaction_update", response["type"]?.jsonPrimitive?.content)
    }

    // -------------------------------------------------------------------------
    // handleAttendantPumpCountUpdate
    // -------------------------------------------------------------------------

    @Test
    fun `handleAttendantPumpCountUpdate sends not_supported ack for each item`() = runTest {
        val data = buildJsonObject {
            put("mode", JsonPrimitive("attendant_pump_count_update"))
            put("data", kotlinx.serialization.json.JsonArray(listOf(
                buildJsonObject {
                    put("PumpNumber", JsonPrimitive(1))
                    put("EmpTagNo", JsonPrimitive("EMP-100"))
                    put("NewMaxTransaction", JsonPrimitive(5))
                },
                buildJsonObject {
                    put("PumpNumber", JsonPrimitive(2))
                    put("EmpTagNo", JsonPrimitive("EMP-200"))
                    put("NewMaxTransaction", JsonPrimitive(10))
                },
            )))
        }
        handler.handleAttendantPumpCountUpdate(session, data)

        assertEquals(2, sentFrames.size)
        for (frame in sentFrames) {
            val response = wsJson.parseToJsonElement(frame).jsonObject
            assertEquals("attendant_pump_count_update_ack", response["type"]?.jsonPrimitive?.content)
        }
    }

    // -------------------------------------------------------------------------
    // handleFuelPumpStatus
    // -------------------------------------------------------------------------

    @Test
    fun `handleFuelPumpStatus sends each pump status individually to session`() = runTest {
        val statuses = listOf(
            stubPumpStatus(pumpNumber = 1, state = PumpState.IDLE),
            stubPumpStatus(pumpNumber = 2, state = PumpState.DISPENSING),
        )
        coEvery { fccAdapter.getPumpStatus() } returns statuses

        handler.handleFuelPumpStatus(session)

        assertEquals(2, sentFrames.size)
        // Verify pump numbers in sent frames
        val pump1 = wsJson.parseToJsonElement(sentFrames[0]).jsonObject
        val pump2 = wsJson.parseToJsonElement(sentFrames[1]).jsonObject
        assertEquals(1, pump1["pump_number"]?.jsonPrimitive?.content?.toInt())
        assertEquals(2, pump2["pump_number"]?.jsonPrimitive?.content?.toInt())
        assertTrue("No broadcast — only sent to requesting session", broadcasts.isEmpty())
    }

    @Test
    fun `handleFuelPumpStatus sends null when adapter not available`() = runTest {
        val handlerNoAdapter = OdooWsMessageHandler(
            bufferManager = bufferManager,
            wsJson = wsJson,
            broadcastToAll = { _, _ -> },
            getFccAdapter = { null },
            getSiteCode = { "SITE001" },
        )

        handlerNoAdapter.handleFuelPumpStatus(session)

        assertEquals(1, sentFrames.size)
        val response = wsJson.parseToJsonElement(sentFrames[0]).jsonObject
        assertEquals("FuelPumpStatus", response["type"]?.jsonPrimitive?.content)
    }

    // -------------------------------------------------------------------------
    // toWsDto — monetary conversion with zero-decimal currency (AF-041 regression)
    // -------------------------------------------------------------------------

    @Test
    fun `BufferedTransaction toWsDto converts correctly for zero-decimal currency TZS`() {
        val tx = stubTransaction(
            amountMinorUnits = 50000L,      // 50000 TZS (no decimal)
            unitPriceMinorPerLitre = 2500L, // 2500 TZS per litre
            volumeMicrolitres = 20_000_000L, // 20 litres
            currencyCode = "TZS",
        )

        val dto = tx.toWsDto()

        // TZS has 0 decimal places → factor is 1.0
        assertEquals(50000.0, dto.total, 0.001)
        assertEquals(2500.0, dto.unitPrice, 0.001)
        assertEquals(20.0, dto.qty, 0.001)
    }

    @Test
    fun `BufferedTransaction toWsDto converts correctly for two-decimal currency USD`() {
        val tx = stubTransaction(
            amountMinorUnits = 5000L,       // $50.00
            unitPriceMinorPerLitre = 250L,  // $2.50 per litre
            volumeMicrolitres = 20_000_000L, // 20 litres
            currencyCode = "USD",
        )

        val dto = tx.toWsDto()

        // USD has 2 decimal places → factor is 100.0
        assertEquals(50.0, dto.total, 0.001)
        assertEquals(2.50, dto.unitPrice, 0.001)
        assertEquals(20.0, dto.qty, 0.001)
    }

    @Test
    fun `BufferedTransaction toWsDto converts correctly for three-decimal currency KWD`() {
        val tx = stubTransaction(
            amountMinorUnits = 15000L,      // 15.000 KWD
            unitPriceMinorPerLitre = 250L,  // 0.250 KWD per litre
            volumeMicrolitres = 60_000_000L, // 60 litres
            currencyCode = "KWD",
        )

        val dto = tx.toWsDto()

        // KWD has 3 decimal places → factor is 1000.0
        assertEquals(15.0, dto.total, 0.001)
        assertEquals(0.25, dto.unitPrice, 0.001)
        assertEquals(60.0, dto.qty, 0.001)
    }

    @Test
    fun `BufferedTransaction toWsDto maps discard state correctly`() {
        val dto = stubTransaction(isDiscard = true).toWsDto()
        assertEquals("discard", dto.state)
    }

    @Test
    fun `BufferedTransaction toWsDto maps synced state to approved`() {
        val dto = stubTransaction(syncStatus = "SYNCED_TO_ODOO").toWsDto()
        assertEquals("approved", dto.state)
    }

    @Test
    fun `BufferedTransaction toWsDto maps pending state`() {
        val dto = stubTransaction(syncStatus = "PENDING").toWsDto()
        assertEquals("pending", dto.state)
        assertEquals(0, dto.syncStatus)
    }

    // -------------------------------------------------------------------------
    // PumpStatus toWsDto
    // -------------------------------------------------------------------------

    @Test
    fun `PumpStatus toWsDto maps all pump states correctly`() {
        val mappings = mapOf(
            PumpState.IDLE to "idle",
            PumpState.AUTHORIZED to "authorized",
            PumpState.CALLING to "calling",
            PumpState.DISPENSING to "dispensing",
            PumpState.PAUSED to "suspended",
            PumpState.COMPLETED to "idle",
            PumpState.ERROR to "inoperative",
            PumpState.OFFLINE to "offline",
            PumpState.UNKNOWN to "unknown",
        )
        for ((state, expected) in mappings) {
            val dto = stubPumpStatus(state = state).toWsDto()
            assertEquals("State $state should map to $expected", expected, dto.status)
        }
    }

    @Test
    fun `PumpStatus toWsDto marks offline and error pumps as not online`() {
        val offline = stubPumpStatus(state = PumpState.OFFLINE).toWsDto()
        assertEquals(false, offline.isOnline)

        val error = stubPumpStatus(state = PumpState.ERROR).toWsDto()
        assertEquals(false, error.isOnline)

        val idle = stubPumpStatus(state = PumpState.IDLE).toWsDto()
        assertEquals(true, idle.isOnline)
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun stubTransaction(
        fccTransactionId: String = "TXN-001",
        pumpNumber: Int = 3,
        nozzleNumber: Int = 1,
        amountMinorUnits: Long = 5000L,
        unitPriceMinorPerLitre: Long = 250L,
        volumeMicrolitres: Long = 20_000_000L,
        currencyCode: String = "USD",
        syncStatus: String = "PENDING",
        isDiscard: Boolean = false,
    ): BufferedTransaction = BufferedTransaction(
        id = "id-001",
        fccTransactionId = fccTransactionId,
        siteCode = "SITE001",
        pumpNumber = pumpNumber,
        nozzleNumber = nozzleNumber,
        productCode = "DIESEL",
        volumeMicrolitres = volumeMicrolitres,
        amountMinorUnits = amountMinorUnits,
        unitPriceMinorPerLitre = unitPriceMinorPerLitre,
        currencyCode = currencyCode,
        startedAt = "2024-06-15T10:00:00Z",
        completedAt = "2024-06-15T10:05:00Z",
        fiscalReceiptNumber = null,
        fccVendor = "RADIX",
        attendantId = "EMP-100",
        status = "PENDING",
        syncStatus = syncStatus,
        ingestionSource = "PUSH",
        rawPayloadJson = null,
        correlationId = "corr-001",
        lastUploadAttemptAt = null,
        lastUploadError = null,
        orderUuid = null,
        odooOrderId = null,
        addToCart = false,
        paymentId = null,
        isDiscard = isDiscard,
        lastFiscalAttemptAt = null,
        acknowledgedAt = null,
        createdAt = "2024-06-15T10:00:00Z",
        updatedAt = "2024-06-15T10:05:00Z",
    )

    private fun stubPumpStatus(
        pumpNumber: Int = 1,
        nozzleNumber: Int = 1,
        state: PumpState = PumpState.IDLE,
    ): PumpStatus = PumpStatus(
        siteCode = "SITE001",
        pumpNumber = pumpNumber,
        nozzleNumber = nozzleNumber,
        state = state,
        currencyCode = "USD",
        statusSequence = 1,
        observedAtUtc = "2024-06-15T10:00:00Z",
        source = PumpStatusSource.FCC_LIVE,
        currentVolumeLitres = "10.5",
        currentAmount = "13.12",
        unitPrice = "1.25",
    )
}

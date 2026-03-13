package com.fccmiddleware.edge.adapter.advatec

import android.util.Log
import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import com.fccmiddleware.edge.adapter.common.CancelPreAuthCommand
import com.fccmiddleware.edge.adapter.common.FccVendor
import com.fccmiddleware.edge.adapter.common.FetchCursor
import com.fccmiddleware.edge.adapter.common.IngestionMode
import com.fccmiddleware.edge.adapter.common.NormalizationResult
import com.fccmiddleware.edge.adapter.common.PreAuthMatchingStrategy
import com.fccmiddleware.edge.adapter.common.RawPayloadEnvelope
import io.mockk.every
import io.mockk.mockkStatic
import io.mockk.unmockkStatic
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.math.BigDecimal
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/**
 * TG-003 — Unit tests for [AdvatecAdapter].
 *
 * Covers:
 *   - normalize: valid receipt → CanonicalTransaction with correct field mappings
 *   - normalize: volume conversion (litres → microlitres)
 *   - normalize: currency factor (TZS factor=1, KWD factor=1000, USD factor=100)
 *   - normalize: product code mapping
 *   - normalize: failure paths (bad dataType, null data, missing TransactionId, empty Items, bad JSON)
 *   - pre-auth correlation: CustomerId match (removes entry from map)
 *   - pre-auth correlation: no FIFO fallback — unmatched receipt is Normal Order (AF-020)
 *   - pre-auth correlation: no match when map is empty
 *   - preAuthMatcher interface contract methods
 *   - cancelPreAuth: null correlationId → false
 *   - cancelPreAuth: matching entry → removes from map and returns true
 *   - cancelPreAuth: non-matching correlationId → false
 */
class AdvatecAdapterTest {

    @Before
    fun setUp() {
        mockkStatic(Log::class)
        every { Log.d(any(), any()) } returns 0
        every { Log.i(any(), any()) } returns 0
        every { Log.w(any(), any<String>()) } returns 0
        every { Log.e(any(), any<String>()) } returns 0
        every { Log.e(any(), any<String>(), any()) } returns 0
    }

    @After
    fun tearDown() {
        unmockkStatic(Log::class)
    }

    // ── Test helpers ─────────────────────────────────────────────────────────

    private fun createConfig(
        currencyCode: String = "TZS",
        siteCode: String = "SITE-001",
        productCodeMapping: Map<String, String> = emptyMap(),
        timezone: String = "Africa/Dar_es_Salaam",
    ): AgentFccConfig = AgentFccConfig(
        fccVendor = FccVendor.ADVATEC,
        connectionProtocol = "HTTP",
        hostAddress = "127.0.0.1",
        port = 5560,
        authCredential = "",
        ingestionMode = IngestionMode.RELAY,
        pullIntervalSeconds = 0,
        siteCode = siteCode,
        productCodeMapping = productCodeMapping,
        timezone = timezone,
        currencyCode = currencyCode,
    )

    private fun rawEnvelope(
        json: String,
        siteCode: String = "SITE-001",
    ): RawPayloadEnvelope = RawPayloadEnvelope(
        vendor = FccVendor.ADVATEC,
        siteCode = siteCode,
        receivedAtUtc = Instant.now().toString(),
        contentType = "application/json",
        payload = json,
    )

    private fun validReceiptJson(
        transactionId: String = "TXN-001",
        date: String = "2024-01-15",
        time: String = "10:30:00",
        amountInclusive: Double = 5000.0,
        quantity: Double = 5.0,
        price: Double = 1000.0,
        amount: Double = 5000.0,
        product: String = "PMS",
        customerId: String? = null,
        receiptCode: String = "RC-001",
    ): String {
        val customerIdField = if (customerId != null) """"CustomerId": "$customerId",""" else ""
        return """
            {
                "DataType": "Receipt",
                "Data": {
                    "Date": "$date",
                    "Time": "$time",
                    "TransactionId": "$transactionId",
                    "ReceiptCode": "$receiptCode",
                    "AmountInclusive": $amountInclusive,
                    $customerIdField
                    "Items": [
                        {
                            "Price": $price,
                            "Amount": $amount,
                            "Quantity": $quantity,
                            "Product": "$product"
                        }
                    ]
                }
            }
        """.trimIndent()
    }

    /** Uses reflection to inject a pre-auth entry into the adapter's private map. */
    private fun injectPreAuth(
        adapter: AdvatecAdapter,
        pumpNumber: Int,
        correlationId: String,
        odooOrderId: String? = null,
        customerId: String? = null,
        ageMillis: Long = 0L,
    ) {
        val field = AdvatecAdapter::class.java.getDeclaredField("activePreAuths")
        field.isAccessible = true
        @Suppress("UNCHECKED_CAST")
        val map = field.get(adapter) as ConcurrentHashMap<Int, ActivePreAuth>
        map[pumpNumber] = ActivePreAuth(
            pumpNumber = pumpNumber,
            correlationId = correlationId,
            odooOrderId = odooOrderId,
            preAuthId = null,
            customerId = customerId,
            customerName = null,
            doseLitres = BigDecimal("10.0"),
            createdAtMillis = System.currentTimeMillis() - ageMillis,
        )
    }

    // ── normalize — valid receipt ─────────────────────────────────────────────

    @Test
    fun `normalize returns Success for a well-formed receipt`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val result = adapter.normalize(rawEnvelope(validReceiptJson()))

        assertTrue(result is NormalizationResult.Success)
    }

    @Test
    fun `normalize sets fccTransactionId as siteCode-transactionId`() = runTest {
        val adapter = AdvatecAdapter(createConfig(siteCode = "TZ-DAR-001"))
        val result = adapter.normalize(rawEnvelope(validReceiptJson(transactionId = "TXN-007"), siteCode = "TZ-DAR-001"))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals("TZ-DAR-001-TXN-007", tx.fccTransactionId)
    }

    @Test
    fun `normalize sets vendor to ADVATEC`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val result = adapter.normalize(rawEnvelope(validReceiptJson()))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals(FccVendor.ADVATEC, tx.fccVendor)
    }

    @Test
    fun `normalize sets fiscal receipt number from ReceiptCode`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val result = adapter.normalize(rawEnvelope(validReceiptJson(receiptCode = "RC-999")))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals("RC-999", tx.fiscalReceiptNumber)
    }

    // ── normalize — volume conversion ─────────────────────────────────────────

    @Test
    fun `normalize converts 5 litres to 5000000 microlitres`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val result = adapter.normalize(rawEnvelope(validReceiptJson(quantity = 5.0)))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals(5_000_000L, tx.volumeMicrolitres)
    }

    @Test
    fun `normalize converts 0_5 litres to 500000 microlitres`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val result = adapter.normalize(rawEnvelope(validReceiptJson(quantity = 0.5, amount = 500.0)))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals(500_000L, tx.volumeMicrolitres)
    }

    // ── normalize — currency factor ───────────────────────────────────────────

    @Test
    fun `normalize TZS factor is 1 — minor units equal major units`() = runTest {
        val adapter = AdvatecAdapter(createConfig(currencyCode = "TZS"))
        val result = adapter.normalize(rawEnvelope(validReceiptJson(amountInclusive = 5000.0, price = 1000.0)))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals(5000L, tx.amountMinorUnits)
        assertEquals(1000L, tx.unitPriceMinorPerLitre)
    }

    @Test
    fun `normalize KWD factor is 1000 — 5_000 major units becomes 5000000 minor`() = runTest {
        val adapter = AdvatecAdapter(createConfig(currencyCode = "KWD"))
        val result = adapter.normalize(rawEnvelope(validReceiptJson(amountInclusive = 5.0, price = 1.0)))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals(5000L, tx.amountMinorUnits)
        assertEquals(1000L, tx.unitPriceMinorPerLitre)
    }

    @Test
    fun `normalize USD factor is 100 — 50_00 major units becomes 5000 minor`() = runTest {
        val adapter = AdvatecAdapter(createConfig(currencyCode = "USD"))
        val result = adapter.normalize(rawEnvelope(validReceiptJson(amountInclusive = 50.0, price = 10.0)))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals(5000L, tx.amountMinorUnits)
        assertEquals(1000L, tx.unitPriceMinorPerLitre)
    }

    // ── normalize — product code mapping ─────────────────────────────────────

    @Test
    fun `normalize applies product code mapping from config`() = runTest {
        val adapter = AdvatecAdapter(createConfig(productCodeMapping = mapOf("PMS" to "PETROL")))
        val result = adapter.normalize(rawEnvelope(validReceiptJson(product = "PMS")))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals("PETROL", tx.productCode)
    }

    @Test
    fun `normalize uses raw product code when no mapping entry found`() = runTest {
        val adapter = AdvatecAdapter(createConfig(productCodeMapping = emptyMap()))
        val result = adapter.normalize(rawEnvelope(validReceiptJson(product = "DIESEL")))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals("DIESEL", tx.productCode)
    }

    // ── normalize — failure paths ─────────────────────────────────────────────

    @Test
    fun `normalize returns UNSUPPORTED_MESSAGE_TYPE for non-Receipt dataType`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val json = """{"DataType": "Customer", "Data": {}}"""
        val result = adapter.normalize(rawEnvelope(json))

        assertTrue(result is NormalizationResult.Failure)
        assertEquals("UNSUPPORTED_MESSAGE_TYPE", (result as NormalizationResult.Failure).errorCode)
    }

    @Test
    fun `normalize returns MISSING_REQUIRED_FIELD when Data is absent`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val json = """{"DataType": "Receipt"}"""
        val result = adapter.normalize(rawEnvelope(json))

        assertTrue(result is NormalizationResult.Failure)
        assertEquals("MISSING_REQUIRED_FIELD", (result as NormalizationResult.Failure).errorCode)
    }

    @Test
    fun `normalize returns MISSING_REQUIRED_FIELD for missing TransactionId`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val json = """
            {
                "DataType": "Receipt",
                "Data": {
                    "AmountInclusive": 1000.0,
                    "Items": [{"Price": 200.0, "Amount": 1000.0, "Quantity": 5.0}]
                }
            }
        """.trimIndent()
        val result = adapter.normalize(rawEnvelope(json))

        assertTrue(result is NormalizationResult.Failure)
        val failure = result as NormalizationResult.Failure
        assertEquals("MISSING_REQUIRED_FIELD", failure.errorCode)
        assertEquals("TransactionId", failure.fieldName)
    }

    @Test
    fun `normalize returns MISSING_REQUIRED_FIELD for empty Items list`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val json = """
            {
                "DataType": "Receipt",
                "Data": {
                    "TransactionId": "TXN-001",
                    "AmountInclusive": 1000.0,
                    "Items": []
                }
            }
        """.trimIndent()
        val result = adapter.normalize(rawEnvelope(json))

        assertTrue(result is NormalizationResult.Failure)
        val failure = result as NormalizationResult.Failure
        assertEquals("MISSING_REQUIRED_FIELD", failure.errorCode)
        assertEquals("Items", failure.fieldName)
    }

    @Test
    fun `normalize returns INVALID_PAYLOAD for malformed JSON`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val result = adapter.normalize(rawEnvelope("{ this is not json }"))

        assertTrue(result is NormalizationResult.Failure)
        assertEquals("INVALID_PAYLOAD", (result as NormalizationResult.Failure).errorCode)
    }

    // ── pre-auth correlation ──────────────────────────────────────────────────

    @Test
    fun `normalize correlates receipt to pre-auth by CustomerId`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        injectPreAuth(adapter, pumpNumber = 1, correlationId = "ADV-1-12345",
            odooOrderId = "ORDER-001", customerId = "TAX-ABC")

        val result = adapter.normalize(rawEnvelope(validReceiptJson(customerId = "TAX-ABC")))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals("ADV-1-12345", tx.correlationId)
        assertEquals("ORDER-001", tx.odooOrderId)
        assertEquals(1, tx.pumpNumber)
    }

    @Test
    fun `AF-020 normalize does NOT FIFO-match when receipt has no CustomerId`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        injectPreAuth(adapter, pumpNumber = 3, correlationId = "ADV-3-FIFO",
            odooOrderId = "ORDER-FIFO", customerId = null)

        // Receipt has no CustomerId — AF-020: no FIFO fallback, treated as Normal Order
        val result = adapter.normalize(rawEnvelope(validReceiptJson()))

        val tx = (result as NormalizationResult.Success).transaction
        // Should NOT match the pre-auth — correlationId is a random UUID, not ADV-3-FIFO
        assertTrue(tx.correlationId != "ADV-3-FIFO")
        assertNull(tx.odooOrderId)
        // Pre-auth should still be in the map (not consumed)
        assertEquals(1, adapter.activePreAuthCount)
    }

    @Test
    fun `AF-020 normalize does not cross-pump match pre-auths`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        // Pre-auth on pump 1
        injectPreAuth(adapter, pumpNumber = 1, correlationId = "ADV-1-PUMP1",
            odooOrderId = "ORDER-PUMP1", customerId = "CUST-A")
        // Pre-auth on pump 2
        injectPreAuth(adapter, pumpNumber = 2, correlationId = "ADV-2-PUMP2",
            odooOrderId = "ORDER-PUMP2", customerId = "CUST-B")

        // Receipt with no CustomerId should NOT steal either pre-auth
        val result = adapter.normalize(rawEnvelope(validReceiptJson()))

        val tx = (result as NormalizationResult.Success).transaction
        assertNull(tx.odooOrderId)
        assertEquals(2, adapter.activePreAuthCount)
    }

    @Test
    fun `normalize removes pre-auth from map after matching`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        injectPreAuth(adapter, pumpNumber = 1, correlationId = "ADV-1-CONSUME",
            customerId = "TAX-CONSUME")

        assertEquals(1, adapter.activePreAuthCount)
        adapter.normalize(rawEnvelope(validReceiptJson(customerId = "TAX-CONSUME")))
        assertEquals(0, adapter.activePreAuthCount)
    }

    @Test
    fun `normalize assigns a random correlationId when no active pre-auth exists`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val result = adapter.normalize(rawEnvelope(validReceiptJson()))

        val tx = (result as NormalizationResult.Success).transaction
        assertNotNull(tx.correlationId)
        assertTrue(tx.correlationId.isNotBlank())
        assertNull(tx.odooOrderId)
    }

    // ── preAuthMatcher interface contract ────────────────────────────────────

    @Test
    fun `preAuthMatcher matchingStrategy is HEURISTIC`() {
        val adapter = AdvatecAdapter(createConfig())
        assertEquals(PreAuthMatchingStrategy.HEURISTIC, adapter.preAuthMatcher.matchingStrategy)
    }

    @Test
    fun `preAuthMatcher matchTransaction returns null when map is empty`() {
        val adapter = AdvatecAdapter(createConfig())
        assertNull(adapter.preAuthMatcher.matchTransaction(1, "TAX-001"))
    }

    @Test
    fun `preAuthMatcher getActivePreAuths returns empty list when no entries`() {
        val adapter = AdvatecAdapter(createConfig())
        assertTrue(adapter.preAuthMatcher.getActivePreAuths().isEmpty())
    }

    @Test
    fun `preAuthMatcher getActivePreAuths returns snapshot of active entries`() {
        val adapter = AdvatecAdapter(createConfig())
        injectPreAuth(adapter, pumpNumber = 2, correlationId = "ADV-2-SNAP",
            odooOrderId = "ORDER-SNAP")

        val snapshots = adapter.preAuthMatcher.getActivePreAuths()
        assertEquals(1, snapshots.size)
        assertEquals("ADV-2-SNAP", snapshots[0].correlationId)
        assertEquals(2, snapshots[0].pumpNumber)
        assertEquals("ORDER-SNAP", snapshots[0].odooOrderId)
    }

    @Test
    fun `preAuthMatcher removePreAuth returns false when correlationId not found`() {
        val adapter = AdvatecAdapter(createConfig())
        assertFalse(adapter.preAuthMatcher.removePreAuth("NON-EXISTENT"))
    }

    @Test
    fun `preAuthMatcher removePreAuth removes entry and returns true`() {
        val adapter = AdvatecAdapter(createConfig())
        injectPreAuth(adapter, pumpNumber = 1, correlationId = "ADV-1-REMOVE")

        assertTrue(adapter.preAuthMatcher.removePreAuth("ADV-1-REMOVE"))
        assertEquals(0, adapter.activePreAuthCount)
    }

    @Test
    fun `preAuthMatcher purgeStale returns 0 when map is empty`() {
        val adapter = AdvatecAdapter(createConfig())
        assertEquals(0, adapter.preAuthMatcher.purgeStale())
    }

    @Test
    fun `preAuthMatcher purgeStale removes entries older than TTL`() {
        val adapter = AdvatecAdapter(createConfig())
        // Inject a pre-auth that is 31 minutes old (TTL is 30 minutes)
        val thirtyOneMinutesMs = 31L * 60 * 1000
        injectPreAuth(adapter, pumpNumber = 5, correlationId = "ADV-5-STALE",
            ageMillis = thirtyOneMinutesMs)

        assertEquals(1, adapter.activePreAuthCount)
        val purged = adapter.preAuthMatcher.purgeStale()
        assertEquals(1, purged)
        assertEquals(0, adapter.activePreAuthCount)
    }

    @Test
    fun `preAuthMatcher purgeStale does not remove fresh entries`() {
        val adapter = AdvatecAdapter(createConfig())
        injectPreAuth(adapter, pumpNumber = 1, correlationId = "ADV-1-FRESH", ageMillis = 0L)

        assertEquals(0, adapter.preAuthMatcher.purgeStale())
        assertEquals(1, adapter.activePreAuthCount)
    }

    // ── cancelPreAuth ────────────────────────────────────────────────────────

    @Test
    fun `cancelPreAuth returns false when fccCorrelationId is null`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val result = adapter.cancelPreAuth(
            CancelPreAuthCommand(siteCode = "SITE-001", pumpNumber = 1, fccCorrelationId = null)
        )
        assertFalse(result)
    }

    @Test
    fun `cancelPreAuth returns false when correlationId not found in active map`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        val result = adapter.cancelPreAuth(
            CancelPreAuthCommand(siteCode = "SITE-001", pumpNumber = 1,
                fccCorrelationId = "ADV-1-GHOST")
        )
        assertFalse(result)
    }

    @Test
    fun `cancelPreAuth removes matching pre-auth and returns true`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        injectPreAuth(adapter, pumpNumber = 1, correlationId = "ADV-1-CANCEL")

        val result = adapter.cancelPreAuth(
            CancelPreAuthCommand(siteCode = "SITE-001", pumpNumber = 1,
                fccCorrelationId = "ADV-1-CANCEL")
        )

        assertTrue(result)
        assertEquals(0, adapter.activePreAuthCount)
    }

    // ── Other interface methods ───────────────────────────────────────────────

    @Test
    fun `acknowledgeTransactions always returns true`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        assertTrue(adapter.acknowledgeTransactions(listOf("TXN-001", "TXN-002")))
    }

    @Test
    fun `fetchTransactions returns empty batch when webhook listener has not started`() = runTest {
        val adapter = AdvatecAdapter(createConfig())
        // Webhook listener won't bind to a real port in unit tests; fetchTransactions
        // handles this gracefully and returns an empty batch.
        val batch = adapter.fetchTransactions(FetchCursor())
        assertTrue(batch.transactions.isEmpty())
        assertFalse(batch.hasMore)
    }
}

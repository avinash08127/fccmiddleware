package com.fccmiddleware.edge.adapter.petronite

import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import com.fccmiddleware.edge.adapter.common.CancelPreAuthCommand
import com.fccmiddleware.edge.adapter.common.FccVendor
import com.fccmiddleware.edge.adapter.common.FetchCursor
import com.fccmiddleware.edge.adapter.common.IngestionMode
import com.fccmiddleware.edge.adapter.common.NormalizationResult
import com.fccmiddleware.edge.adapter.common.PreAuthMatchingStrategy
import com.fccmiddleware.edge.adapter.common.RawPayloadEnvelope
import io.ktor.client.HttpClient
import io.ktor.client.engine.mock.MockEngine
import io.ktor.client.engine.mock.respond
import io.ktor.http.HttpStatusCode
import io.ktor.http.headersOf
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/**
 * TG-003 — Unit tests for [PetroniteAdapter].
 *
 * Covers:
 *   - normalize: valid transaction.completed webhook → CanonicalTransaction
 *   - normalize: volume conversion (litres → microlitres)
 *   - normalize: currency factor (TZS factor=1, KWD factor=1000, USD factor=100)
 *   - normalize: PUMA_ORDER payment method correlates with active pre-auth
 *   - normalize: wrong event type → Failure
 *   - normalize: missing transaction → Failure
 *   - normalize: blank volumeLitres → Failure
 *   - normalize: malformed amountMajor → Failure
 *   - heartbeat: succeeds when /nozzles/assigned returns 200
 *   - heartbeat: fails when /nozzles/assigned returns non-2xx
 *   - heartbeat: retries after 401 and succeeds
 *   - cancelPreAuth: 200 → true
 *   - cancelPreAuth: 404 → true (idempotent)
 *   - cancelPreAuth: null correlationId → false
 *   - preAuthMatcher: DETERMINISTIC strategy, matchTransaction, removePreAuth
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class PetroniteAdapterTest {

    // ── Test helpers ─────────────────────────────────────────────────────────

    private val baseUrl = "https://petronite.test"
    private val oauthUrl = "$baseUrl/oauth/token"

    private fun createConfig(): AgentFccConfig = AgentFccConfig(
        fccVendor = FccVendor.PETRONITE,
        connectionProtocol = "REST_JSON",
        hostAddress = baseUrl,
        port = 443,
        authCredential = "",
        ingestionMode = IngestionMode.RELAY,
        pullIntervalSeconds = 0,
        siteCode = "SITE-001",
        productCodeMapping = emptyMap(),
        timezone = "Africa/Dar_es_Salaam",
        currencyCode = "TZS",
        clientId = "test-client",
        clientSecret = "test-secret",
        oauthTokenEndpoint = oauthUrl,
    )

    private fun tokenResponse(): String =
        """{"access_token":"mock-token","token_type":"Bearer","expires_in":3600}"""

    private fun createMockEngine(vararg handlers: Pair<String, () -> String>): MockEngine {
        val responseMap = handlers.toMap()
        return MockEngine { request ->
            val path = request.url.encodedPath
            val body = responseMap[path]?.invoke() ?: ""
            val status = if (responseMap.containsKey(path)) HttpStatusCode.OK else HttpStatusCode.NotFound
            respond(
                content = body,
                status = status,
                headers = headersOf("Content-Type" to listOf("application/json")),
            )
        }
    }

    private fun rawEnvelope(
        json: String,
        siteCode: String = "SITE-001",
    ): RawPayloadEnvelope = RawPayloadEnvelope(
        vendor = FccVendor.PETRONITE,
        siteCode = siteCode,
        receivedAtUtc = Instant.now().toString(),
        contentType = "application/json",
        payload = json,
    )

    private fun validWebhookJson(
        orderId: String = "ORDER-001",
        nozzleId: String = "NOZZLE-1",
        pumpNumber: Int = 1,
        nozzleNumber: Int = 1,
        volumeLitres: String = "10.0",
        amountMajor: String = "5000",
        unitPrice: String = "500",
        currency: String = "TZS",
        paymentMethod: String = "CASH",
        startTime: String = "2024-01-15T10:00:00Z",
        endTime: String = "2024-01-15T10:05:00Z",
    ): String = """
        {
            "eventType": "transaction.completed",
            "timestamp": "2024-01-15T10:05:00Z",
            "transaction": {
                "orderId": "$orderId",
                "nozzleId": "$nozzleId",
                "pumpNumber": $pumpNumber,
                "nozzleNumber": $nozzleNumber,
                "productCode": "PMS",
                "volumeLitres": "$volumeLitres",
                "amountMajor": "$amountMajor",
                "unitPrice": "$unitPrice",
                "currency": "$currency",
                "startTime": "$startTime",
                "endTime": "$endTime",
                "paymentMethod": "$paymentMethod"
            }
        }
    """.trimIndent()

    /** Injects a pre-auth into the adapter's private activePreAuths map. */
    private fun injectPreAuth(
        adapter: PetroniteAdapter,
        orderId: String,
        nozzleId: String = "NOZZLE-1",
        pumpNumber: Int = 1,
        odooOrderId: String? = null,
        ageMillis: Long = 0L,
    ) {
        val field = PetroniteAdapter::class.java.getDeclaredField("activePreAuths")
        field.isAccessible = true
        @Suppress("UNCHECKED_CAST")
        val map = field.get(adapter) as ConcurrentHashMap<String, ActivePreAuth>
        map[orderId] = ActivePreAuth(
            orderId = orderId,
            nozzleId = nozzleId,
            pumpNumber = pumpNumber,
            odooOrderId = odooOrderId,
            preAuthId = null,
            createdAtMillis = System.currentTimeMillis() - ageMillis,
        )
    }

    // ── normalize — valid transaction ────────────────────────────────────────

    @Test
    fun `normalize returns Success for a valid transaction_completed webhook`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(validWebhookJson()))

        assertTrue(result is NormalizationResult.Success)
    }

    @Test
    fun `normalize sets fccTransactionId as siteCode-orderId`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(validWebhookJson(orderId = "ORD-999"), siteCode = "TZ-001"))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals("TZ-001-ORD-999", tx.fccTransactionId)
    }

    @Test
    fun `normalize sets vendor to PETRONITE`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(validWebhookJson()))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals(FccVendor.PETRONITE, tx.fccVendor)
    }

    // ── normalize — volume conversion ─────────────────────────────────────────

    @Test
    fun `normalize converts 10 litres to 10000000 microlitres`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(validWebhookJson(volumeLitres = "10.0")))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals(10_000_000L, tx.volumeMicrolitres)
    }

    @Test
    fun `normalize converts 0_5 litres to 500000 microlitres`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(validWebhookJson(volumeLitres = "0.5")))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals(500_000L, tx.volumeMicrolitres)
    }

    // ── normalize — currency factor ───────────────────────────────────────────

    @Test
    fun `normalize TZS factor is 1 — amount minor units equal major units`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(
            validWebhookJson(amountMajor = "5000", unitPrice = "500", currency = "TZS")
        ))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals(5000L, tx.amountMinorUnits)
        assertEquals(500L, tx.unitPriceMinorPerLitre)
    }

    @Test
    fun `normalize KWD factor is 1000 — 5_000 major becomes 5000000 minor`() = runTest {
        val config = createConfig().copy(currencyCode = "KWD")
        val adapter = PetroniteAdapter(config, HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(
            validWebhookJson(amountMajor = "5.000", unitPrice = "1.000", currency = "KWD")
        ))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals(5000L, tx.amountMinorUnits)
        assertEquals(1000L, tx.unitPriceMinorPerLitre)
    }

    @Test
    fun `normalize USD factor is 100 — 50_00 major becomes 5000 minor`() = runTest {
        val config = createConfig().copy(currencyCode = "USD")
        val adapter = PetroniteAdapter(config, HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(
            validWebhookJson(amountMajor = "50.00", unitPrice = "10.00", currency = "USD")
        ))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals(5000L, tx.amountMinorUnits)
        assertEquals(1000L, tx.unitPriceMinorPerLitre)
    }

    // ── normalize — PUMA_ORDER pre-auth correlation ───────────────────────────

    @Test
    fun `normalize correlates PUMA_ORDER transaction with active pre-auth`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        injectPreAuth(adapter, orderId = "ORDER-001", odooOrderId = "ODOO-123")

        val result = adapter.normalize(rawEnvelope(
            validWebhookJson(orderId = "ORDER-001", paymentMethod = "PUMA_ORDER")
        ))

        val tx = (result as NormalizationResult.Success).transaction
        assertEquals("ORDER-001", tx.correlationId)
        assertEquals("ODOO-123", tx.odooOrderId)
    }

    @Test
    fun `normalize removes pre-auth from map after PUMA_ORDER correlation`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        injectPreAuth(adapter, orderId = "ORDER-002")

        assertEquals(1, adapter.activePreAuthCount)
        adapter.normalize(rawEnvelope(validWebhookJson(orderId = "ORDER-002", paymentMethod = "PUMA_ORDER")))
        assertEquals(0, adapter.activePreAuthCount)
    }

    @Test
    fun `normalize assigns random correlationId for non-PUMA_ORDER payment`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(validWebhookJson(paymentMethod = "CASH")))

        val tx = (result as NormalizationResult.Success).transaction
        assertNotNull(tx.correlationId)
        assertNull(tx.odooOrderId)
    }

    // ── normalize — failure paths ─────────────────────────────────────────────

    @Test
    fun `normalize returns UNSUPPORTED_MESSAGE_TYPE for wrong event type`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val json = """{"eventType": "pump.status", "timestamp": "2024-01-15T10:00:00Z"}"""
        val result = adapter.normalize(rawEnvelope(json))

        assertTrue(result is NormalizationResult.Failure)
        assertEquals("UNSUPPORTED_MESSAGE_TYPE", (result as NormalizationResult.Failure).errorCode)
    }

    @Test
    fun `normalize returns MISSING_REQUIRED_FIELD when transaction is absent`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val json = """{"eventType": "transaction.completed", "timestamp": "2024-01-15T10:00:00Z"}"""
        val result = adapter.normalize(rawEnvelope(json))

        assertTrue(result is NormalizationResult.Failure)
        assertEquals("MISSING_REQUIRED_FIELD", (result as NormalizationResult.Failure).errorCode)
    }

    @Test
    fun `normalize returns MISSING_REQUIRED_FIELD for blank volumeLitres`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(validWebhookJson(volumeLitres = "")))

        assertTrue(result is NormalizationResult.Failure)
        val failure = result as NormalizationResult.Failure
        assertEquals("MISSING_REQUIRED_FIELD", failure.errorCode)
        assertEquals("volumeLitres", failure.fieldName)
    }

    @Test
    fun `normalize returns MALFORMED_FIELD for non-numeric volumeLitres`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(validWebhookJson(volumeLitres = "not-a-number")))

        assertTrue(result is NormalizationResult.Failure)
        assertEquals("MALFORMED_FIELD", (result as NormalizationResult.Failure).errorCode)
    }

    @Test
    fun `normalize returns MISSING_REQUIRED_FIELD for blank amountMajor`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(validWebhookJson(amountMajor = "")))

        assertTrue(result is NormalizationResult.Failure)
        assertEquals("MISSING_REQUIRED_FIELD", (result as NormalizationResult.Failure).errorCode)
    }

    @Test
    fun `normalize returns MALFORMED_FIELD for non-numeric amountMajor`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope(validWebhookJson(amountMajor = "invalid")))

        assertTrue(result is NormalizationResult.Failure)
        assertEquals("MALFORMED_FIELD", (result as NormalizationResult.Failure).errorCode)
    }

    @Test
    fun `normalize returns INVALID_PAYLOAD for malformed JSON`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val result = adapter.normalize(rawEnvelope("this is not json"))

        assertTrue(result is NormalizationResult.Failure)
        assertEquals("INVALID_PAYLOAD", (result as NormalizationResult.Failure).errorCode)
    }

    // ── heartbeat ────────────────────────────────────────────────────────────

    @Test
    fun `heartbeat returns true when GET nozzles-assigned returns 200`() = runTest {
        val mockEngine = MockEngine { request ->
            when (request.url.encodedPath) {
                "/oauth/token" -> respond(
                    content = tokenResponse(),
                    status = HttpStatusCode.OK,
                    headers = headersOf("Content-Type" to listOf("application/json")),
                )
                "/nozzles/assigned" -> respond(
                    content = "[]",
                    status = HttpStatusCode.OK,
                    headers = headersOf("Content-Type" to listOf("application/json")),
                )
                else -> respond("", HttpStatusCode.NotFound)
            }
        }
        val adapter = PetroniteAdapter(createConfig(), HttpClient(mockEngine))

        assertTrue(adapter.heartbeat())
    }

    @Test
    fun `heartbeat returns false when GET nozzles-assigned returns 503`() = runTest {
        val mockEngine = MockEngine { request ->
            when (request.url.encodedPath) {
                "/oauth/token" -> respond(
                    content = tokenResponse(),
                    status = HttpStatusCode.OK,
                    headers = headersOf("Content-Type" to listOf("application/json")),
                )
                "/nozzles/assigned" -> respond("Service Unavailable", HttpStatusCode.ServiceUnavailable)
                else -> respond("", HttpStatusCode.NotFound)
            }
        }
        val adapter = PetroniteAdapter(createConfig(), HttpClient(mockEngine))

        assertFalse(adapter.heartbeat())
    }

    @Test
    fun `heartbeat retries after 401 and returns true on success`() = runTest {
        var callCount = 0
        val mockEngine = MockEngine { request ->
            when (request.url.encodedPath) {
                "/oauth/token" -> respond(
                    content = tokenResponse(),
                    status = HttpStatusCode.OK,
                    headers = headersOf("Content-Type" to listOf("application/json")),
                )
                "/nozzles/assigned" -> {
                    callCount++
                    if (callCount == 1) {
                        respond("Unauthorized", HttpStatusCode.Unauthorized)
                    } else {
                        respond("[]", HttpStatusCode.OK,
                            headers = headersOf("Content-Type" to listOf("application/json")))
                    }
                }
                else -> respond("", HttpStatusCode.NotFound)
            }
        }
        val adapter = PetroniteAdapter(createConfig(), HttpClient(mockEngine))

        assertTrue(adapter.heartbeat())
        assertEquals(2, callCount)
    }

    // ── cancelPreAuth ────────────────────────────────────────────────────────

    @Test
    fun `cancelPreAuth returns false when fccCorrelationId is null`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        assertFalse(adapter.cancelPreAuth(
            CancelPreAuthCommand(siteCode = "SITE-001", pumpNumber = 1, fccCorrelationId = null)
        ))
    }

    @Test
    fun `cancelPreAuth returns true when cancel endpoint returns 200`() = runTest {
        val mockEngine = MockEngine { request ->
            when (request.url.encodedPath) {
                "/oauth/token" -> respond(
                    content = tokenResponse(),
                    status = HttpStatusCode.OK,
                    headers = headersOf("Content-Type" to listOf("application/json")),
                )
                "/direct-authorize-requests/ORDER-001/cancel" -> respond("", HttpStatusCode.OK)
                else -> respond("", HttpStatusCode.NotFound)
            }
        }
        val adapter = PetroniteAdapter(createConfig(), HttpClient(mockEngine))

        assertTrue(adapter.cancelPreAuth(
            CancelPreAuthCommand(siteCode = "SITE-001", pumpNumber = 1,
                fccCorrelationId = "ORDER-001")
        ))
    }

    @Test
    fun `cancelPreAuth returns true for 404 — idempotent cancel`() = runTest {
        val mockEngine = MockEngine { request ->
            when (request.url.encodedPath) {
                "/oauth/token" -> respond(
                    content = tokenResponse(),
                    status = HttpStatusCode.OK,
                    headers = headersOf("Content-Type" to listOf("application/json")),
                )
                "/direct-authorize-requests/ORDER-404/cancel" -> respond(
                    "Not found", HttpStatusCode.NotFound
                )
                else -> respond("", HttpStatusCode.NotFound)
            }
        }
        val adapter = PetroniteAdapter(createConfig(), HttpClient(mockEngine))

        assertTrue(adapter.cancelPreAuth(
            CancelPreAuthCommand(siteCode = "SITE-001", pumpNumber = 1,
                fccCorrelationId = "ORDER-404")
        ))
    }

    @Test
    fun `cancelPreAuth returns false on 500 server error`() = runTest {
        val mockEngine = MockEngine { request ->
            when (request.url.encodedPath) {
                "/oauth/token" -> respond(
                    content = tokenResponse(),
                    status = HttpStatusCode.OK,
                    headers = headersOf("Content-Type" to listOf("application/json")),
                )
                "/direct-authorize-requests/ORDER-500/cancel" -> respond(
                    "Internal Server Error", HttpStatusCode.InternalServerError
                )
                else -> respond("", HttpStatusCode.NotFound)
            }
        }
        val adapter = PetroniteAdapter(createConfig(), HttpClient(mockEngine))

        assertFalse(adapter.cancelPreAuth(
            CancelPreAuthCommand(siteCode = "SITE-001", pumpNumber = 1,
                fccCorrelationId = "ORDER-500")
        ))
    }

    // ── preAuthMatcher interface contract ────────────────────────────────────

    @Test
    fun `preAuthMatcher matchingStrategy is DETERMINISTIC`() {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        assertEquals(PreAuthMatchingStrategy.DETERMINISTIC, adapter.preAuthMatcher.matchingStrategy)
    }

    @Test
    fun `preAuthMatcher matchTransaction returns null when map is empty`() {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        assertNull(adapter.preAuthMatcher.matchTransaction(1, "ORDER-001"))
    }

    @Test
    fun `preAuthMatcher matchTransaction returns null when vendorMatchKey is null`() {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        assertNull(adapter.preAuthMatcher.matchTransaction(1, null))
    }

    @Test
    fun `preAuthMatcher removePreAuth returns false when correlationId not found`() {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        assertFalse(adapter.preAuthMatcher.removePreAuth("PETRONITE-GHOST"))
    }

    @Test
    fun `preAuthMatcher getActivePreAuths returns empty list when no pre-auths registered`() {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        assertTrue(adapter.preAuthMatcher.getActivePreAuths().isEmpty())
    }

    // ── fetchTransactions (push-only no-op) ──────────────────────────────────

    @Test
    fun `fetchTransactions always returns empty batch for push-only adapter`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        val batch = adapter.fetchTransactions(FetchCursor())

        assertTrue(batch.transactions.isEmpty())
        assertFalse(batch.hasMore)
    }

    // ── acknowledgeTransactions (no-op) ──────────────────────────────────────

    @Test
    fun `acknowledgeTransactions always returns true`() = runTest {
        val adapter = PetroniteAdapter(createConfig(), HttpClient(MockEngine { respond("") }))
        assertTrue(adapter.acknowledgeTransactions(listOf("TXN-001")))
    }
}

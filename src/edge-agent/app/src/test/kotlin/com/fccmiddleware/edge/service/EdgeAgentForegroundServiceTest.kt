package com.fccmiddleware.edge.service

import android.app.Service
import android.content.Intent
import com.fccmiddleware.edge.adapter.common.IFccAdapterFactory
import com.fccmiddleware.edge.api.LocalApiServer
import com.fccmiddleware.edge.buffer.IntegrityChecker
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.LocalOverrideManager
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.connectivity.NetworkBinder
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.logging.StructuredFileLogger
import com.fccmiddleware.edge.preauth.PreAuthHandler
import com.fccmiddleware.edge.runtime.CadenceController
import com.fccmiddleware.edge.runtime.FccRuntimeState
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.DeviceTokenProvider
import com.fccmiddleware.edge.websocket.OdooWebSocketServer
import io.mockk.mockk
import io.mockk.verify
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.koin.core.context.startKoin
import org.koin.core.context.stopKoin
import org.koin.dsl.module
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.util.concurrent.atomic.AtomicBoolean

/**
 * TG-004 — Robolectric unit tests for [EdgeAgentForegroundService].
 *
 * Covers:
 *   - onCreate creates the notification channel
 *   - onStartCommand returns START_STICKY
 *   - Re-entrant guard (AtomicBoolean) prevents duplicate initialization
 *   - onDestroy resets serviceStarted to false, allowing a future clean start
 *   - onBind always returns null (not a bound service)
 *   - networkBinder.start() and connectivityManager.start() are called on first start
 *   - Duplicate onStartCommand does NOT call networkBinder.start() a second time
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class EdgeAgentForegroundServiceTest {

    // All 15 Koin-injected dependencies
    private lateinit var localApiServer: LocalApiServer
    private lateinit var connectivityManager: ConnectivityManager
    private lateinit var cadenceController: CadenceController
    private lateinit var configManager: ConfigManager
    private lateinit var fccAdapterFactory: IFccAdapterFactory
    private lateinit var cloudApiClient: CloudApiClient
    private lateinit var encryptedPrefs: EncryptedPrefsManager
    private lateinit var fccRuntimeState: FccRuntimeState
    private lateinit var ingestionOrchestrator: IngestionOrchestrator
    private lateinit var preAuthHandler: PreAuthHandler
    private lateinit var tokenProvider: DeviceTokenProvider
    private lateinit var fileLogger: StructuredFileLogger
    private lateinit var localOverrideManager: LocalOverrideManager
    private lateinit var networkBinder: NetworkBinder
    private lateinit var odooWebSocketServer: OdooWebSocketServer
    private lateinit var integrityChecker: IntegrityChecker

    @Before
    fun setUp() {
        localApiServer = mockk(relaxed = true)
        connectivityManager = mockk(relaxed = true)
        cadenceController = mockk(relaxed = true)
        configManager = mockk(relaxed = true)
        fccAdapterFactory = mockk(relaxed = true)
        cloudApiClient = mockk(relaxed = true)
        encryptedPrefs = mockk(relaxed = true)
        fccRuntimeState = mockk(relaxed = true)
        ingestionOrchestrator = mockk(relaxed = true)
        preAuthHandler = mockk(relaxed = true)
        tokenProvider = mockk(relaxed = true)
        fileLogger = mockk(relaxed = true)
        localOverrideManager = mockk(relaxed = true)
        networkBinder = mockk(relaxed = true)
        odooWebSocketServer = mockk(relaxed = true)
        integrityChecker = mockk(relaxed = true)

        // AF-038: Default IntegrityChecker.runCheck() to return Healthy so existing tests pass
        io.mockk.coEvery { integrityChecker.runCheck() } returns IntegrityChecker.IntegrityCheckResult.Healthy

        // Wire ConfigManager.config to a MutableStateFlow so collect() does not hang
        val configFlow = MutableStateFlow<com.fccmiddleware.edge.config.EdgeAgentConfigDto?>(null)
        io.mockk.every { configManager.config } returns configFlow

        startKoin {
            modules(module {
                // AT-003: Service now injects the Koin-managed CoroutineScope
                single<CoroutineScope> { CoroutineScope(SupervisorJob() + Dispatchers.IO) }
                single { localApiServer }
                single { connectivityManager }
                single { cadenceController }
                single { configManager }
                single { fccAdapterFactory }
                single { cloudApiClient }
                single { encryptedPrefs }
                single { fccRuntimeState }
                single { ingestionOrchestrator }
                single { preAuthHandler }
                single { tokenProvider }
                single { fileLogger }
                single { localOverrideManager }
                single { networkBinder }
                single { odooWebSocketServer }
                single { integrityChecker }
            })
        }
    }

    @After
    fun tearDown() {
        stopKoin()
    }

    // ── onBind ───────────────────────────────────────────────────────────────

    @Test
    fun `onBind returns null — service is not bound`() {
        val service = Robolectric.buildService(EdgeAgentForegroundService::class.java)
            .create().get()

        assertNull(service.onBind(Intent()))
    }

    // ── onStartCommand ───────────────────────────────────────────────────────

    @Test
    fun `onStartCommand returns START_STICKY`() {
        val service = Robolectric.buildService(EdgeAgentForegroundService::class.java)
            .create().get()

        val result = service.onStartCommand(Intent(), 0, 1)

        assertEquals(Service.START_STICKY, result)
    }

    @Test
    fun `second onStartCommand also returns START_STICKY`() {
        val service = Robolectric.buildService(EdgeAgentForegroundService::class.java)
            .create().get()

        service.onStartCommand(Intent(), 0, 1)
        val result = service.onStartCommand(Intent(), 0, 2)

        assertEquals(Service.START_STICKY, result)
    }

    // ── re-entrant guard ─────────────────────────────────────────────────────

    @Test
    fun `first onStartCommand calls networkBinder_start`() {
        val service = Robolectric.buildService(EdgeAgentForegroundService::class.java)
            .create().get()

        service.onStartCommand(Intent(), 0, 1)

        verify(atLeast = 1) { networkBinder.start() }
    }

    @Test
    fun `second onStartCommand does NOT call networkBinder_start again`() {
        val service = Robolectric.buildService(EdgeAgentForegroundService::class.java)
            .create().get()

        service.onStartCommand(Intent(), 0, 1)
        service.onStartCommand(Intent(), 0, 2)

        // Guard ensures networkBinder.start() is called exactly once
        verify(exactly = 1) { networkBinder.start() }
    }

    @Test
    fun `re-entrant guard is set to true after first onStartCommand`() {
        val service = Robolectric.buildService(EdgeAgentForegroundService::class.java)
            .create().get()

        service.onStartCommand(Intent(), 0, 1)

        val field = EdgeAgentForegroundService::class.java.getDeclaredField("serviceStarted")
        field.isAccessible = true
        val started = field.get(service) as AtomicBoolean
        assertTrue(started.get())
    }

    // ── onDestroy ────────────────────────────────────────────────────────────

    @Test
    fun `onDestroy resets serviceStarted flag to false`() {
        val service = Robolectric.buildService(EdgeAgentForegroundService::class.java)
            .create().get()

        service.onStartCommand(Intent(), 0, 1)

        val field = EdgeAgentForegroundService::class.java.getDeclaredField("serviceStarted")
        field.isAccessible = true
        val started = field.get(service) as AtomicBoolean

        assertTrue("serviceStarted should be true after first start", started.get())

        service.onDestroy()

        assertFalse("serviceStarted should be false after destroy", started.get())
    }

    @Test
    fun `onDestroy stops cadenceController`() {
        val service = Robolectric.buildService(EdgeAgentForegroundService::class.java)
            .create().get()
        service.onStartCommand(Intent(), 0, 1)

        service.onDestroy()

        verify { cadenceController.stop() }
    }

    @Test
    fun `onDestroy stops connectivityManager`() {
        val service = Robolectric.buildService(EdgeAgentForegroundService::class.java)
            .create().get()
        service.onStartCommand(Intent(), 0, 1)

        service.onDestroy()

        verify { connectivityManager.stop() }
    }

    @Test
    fun `onDestroy stops networkBinder`() {
        val service = Robolectric.buildService(EdgeAgentForegroundService::class.java)
            .create().get()
        service.onStartCommand(Intent(), 0, 1)

        service.onDestroy()

        verify { networkBinder.stop() }
    }

    @Test
    fun `onDestroy stops localApiServer`() {
        val service = Robolectric.buildService(EdgeAgentForegroundService::class.java)
            .create().get()
        service.onStartCommand(Intent(), 0, 1)

        service.onDestroy()

        verify { localApiServer.stop() }
    }

    @Test
    fun `after onDestroy a fresh onStartCommand re-initializes normally`() {
        val service = Robolectric.buildService(EdgeAgentForegroundService::class.java)
            .create().get()

        // Full cycle: start, destroy, start again
        service.onStartCommand(Intent(), 0, 1)
        service.onDestroy()
        service.onStartCommand(Intent(), 0, 2)

        // networkBinder.start() should have been called twice — once per lifecycle
        verify(exactly = 2) { networkBinder.start() }
    }

    // ── CHANNEL_ID / NOTIFICATION_ID constants ────────────────────────────────

    @Test
    fun `CHANNEL_ID constant is correct`() {
        assertEquals("fcc_edge_agent_channel", EdgeAgentForegroundService.CHANNEL_ID)
    }

    @Test
    fun `NOTIFICATION_ID constant is 1`() {
        assertEquals(1, EdgeAgentForegroundService.NOTIFICATION_ID)
    }

    // ── helper ────────────────────────────────────────────────────────────────

    private fun assertNull(value: Any?) {
        org.junit.Assert.assertNull(value)
    }
}

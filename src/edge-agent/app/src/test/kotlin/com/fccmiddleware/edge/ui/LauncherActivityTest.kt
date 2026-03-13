package com.fccmiddleware.edge.ui

import android.content.Intent
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import io.mockk.every
import io.mockk.mockk
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.koin.core.context.startKoin
import org.koin.core.context.stopKoin
import org.koin.dsl.module
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows
import org.robolectric.annotation.Config

/**
 * TG-001 — Robolectric unit tests for [LauncherActivity] routing logic.
 *
 * Covers the three routing branches and the exception-fallback path:
 *   1. isDecommissioned=true  → DecommissionedActivity
 *   2. isRegistered=true      → DiagnosticsActivity (foreground service started)
 *   3. isRegistered=false     → ProvisioningActivity
 *   4. Exception reading prefs → ProvisioningActivity (safe fallback)
 *
 * Also verifies that:
 *   - The activity always calls finish() after routing
 *   - FLAG_ACTIVITY_NEW_TASK | FLAG_ACTIVITY_CLEAR_TASK is set on the target intent
 *   - The foreground service is started only on the registered path
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class LauncherActivityTest {

    private lateinit var encryptedPrefs: EncryptedPrefsManager

    @Before
    fun setUp() {
        encryptedPrefs = mockk(relaxed = true)
        startKoin {
            modules(module {
                single { encryptedPrefs }
            })
        }
    }

    @After
    fun tearDown() {
        stopKoin()
    }

    // ── decommissioned path ──────────────────────────────────────────────────

    @Test
    fun `routes to DecommissionedActivity when isDecommissioned is true`() {
        every { encryptedPrefs.isDecommissioned } returns true

        val controller = Robolectric.buildActivity(LauncherActivity::class.java).create()
        val shadow = Shadows.shadowOf(controller.get())

        val next = shadow.nextStartedActivity
        assertNotNull(next)
        assertEquals(
            DecommissionedActivity::class.java.name,
            next.component?.className,
        )
    }

    @Test
    fun `activity finishes after routing to DecommissionedActivity`() {
        every { encryptedPrefs.isDecommissioned } returns true

        val controller = Robolectric.buildActivity(LauncherActivity::class.java).create()
        assertTrue(controller.get().isFinishing)
    }

    @Test
    fun `decommissioned intent carries CLEAR_TASK and NEW_TASK flags`() {
        every { encryptedPrefs.isDecommissioned } returns true

        val controller = Robolectric.buildActivity(LauncherActivity::class.java).create()
        val next = Shadows.shadowOf(controller.get()).nextStartedActivity

        val expectedFlags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
        assertEquals(expectedFlags, next.flags and expectedFlags)
    }

    // ── registered path ──────────────────────────────────────────────────────

    @Test
    fun `routes to DiagnosticsActivity when isRegistered is true`() {
        every { encryptedPrefs.isDecommissioned } returns false
        every { encryptedPrefs.isRegistered } returns true

        val controller = Robolectric.buildActivity(LauncherActivity::class.java).create()
        val shadow = Shadows.shadowOf(controller.get())

        val next = shadow.nextStartedActivity
        assertNotNull(next)
        assertEquals(
            DiagnosticsActivity::class.java.name,
            next.component?.className,
        )
    }

    @Test
    fun `starts EdgeAgentForegroundService when isRegistered is true`() {
        every { encryptedPrefs.isDecommissioned } returns false
        every { encryptedPrefs.isRegistered } returns true

        Robolectric.buildActivity(LauncherActivity::class.java).create()

        // startForegroundService is captured at the application shadow level
        val appShadow = Shadows.shadowOf(RuntimeEnvironment.getApplication())
        val startedService = appShadow.nextStartedService
        assertNotNull(startedService)
        assertEquals(
            EdgeAgentForegroundService::class.java.name,
            startedService.component?.className,
        )
    }

    @Test
    fun `activity finishes after routing to DiagnosticsActivity`() {
        every { encryptedPrefs.isDecommissioned } returns false
        every { encryptedPrefs.isRegistered } returns true

        val controller = Robolectric.buildActivity(LauncherActivity::class.java).create()
        assertTrue(controller.get().isFinishing)
    }

    // ── not-registered path ──────────────────────────────────────────────────

    @Test
    fun `routes to ProvisioningActivity when device is not registered`() {
        every { encryptedPrefs.isDecommissioned } returns false
        every { encryptedPrefs.isRegistered } returns false

        val controller = Robolectric.buildActivity(LauncherActivity::class.java).create()
        val next = Shadows.shadowOf(controller.get()).nextStartedActivity

        assertNotNull(next)
        assertEquals(
            ProvisioningActivity::class.java.name,
            next.component?.className,
        )
    }

    @Test
    fun `does NOT start foreground service when device is not registered`() {
        every { encryptedPrefs.isDecommissioned } returns false
        every { encryptedPrefs.isRegistered } returns false

        Robolectric.buildActivity(LauncherActivity::class.java).create()

        // No service should have been started on the unregistered path
        val appShadow = Shadows.shadowOf(RuntimeEnvironment.getApplication())
        assertNull(appShadow.nextStartedService)
    }

    @Test
    fun `activity finishes after routing to ProvisioningActivity`() {
        every { encryptedPrefs.isDecommissioned } returns false
        every { encryptedPrefs.isRegistered } returns false

        val controller = Robolectric.buildActivity(LauncherActivity::class.java).create()
        assertTrue(controller.get().isFinishing)
    }

    // ── exception-fallback path ───────────────────────────────────────────────

    @Test
    fun `falls back to ProvisioningActivity when encryptedPrefs throws exception`() {
        every { encryptedPrefs.isDecommissioned } throws RuntimeException("Keystore corruption")

        val controller = Robolectric.buildActivity(LauncherActivity::class.java).create()
        val next = Shadows.shadowOf(controller.get()).nextStartedActivity

        assertNotNull(next)
        assertEquals(
            ProvisioningActivity::class.java.name,
            next.component?.className,
        )
    }

    @Test
    fun `activity still finishes after exception fallback`() {
        every { encryptedPrefs.isDecommissioned } throws RuntimeException("Keystore corruption")

        val controller = Robolectric.buildActivity(LauncherActivity::class.java).create()
        assertTrue(controller.get().isFinishing)
    }

    @Test
    fun `falls back to ProvisioningActivity when isRegistered throws exception`() {
        every { encryptedPrefs.isDecommissioned } returns false
        every { encryptedPrefs.isRegistered } throws IllegalStateException("EncryptedSharedPreferences not ready")

        val controller = Robolectric.buildActivity(LauncherActivity::class.java).create()
        val next = Shadows.shadowOf(controller.get()).nextStartedActivity

        assertNotNull(next)
        assertEquals(
            ProvisioningActivity::class.java.name,
            next.component?.className,
        )
    }

    // ── helper ────────────────────────────────────────────────────────────────

    private fun assertNotNull(value: Any?) {
        org.junit.Assert.assertNotNull(value)
    }

    private fun assertNull(value: Any?) {
        org.junit.Assert.assertNull(value)
    }
}

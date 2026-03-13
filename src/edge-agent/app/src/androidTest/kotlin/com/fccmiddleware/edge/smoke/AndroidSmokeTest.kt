package com.fccmiddleware.edge.smoke

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith

/**
 * TG-002 — Instrumented (androidTest) smoke tests.
 *
 * This directory establishes the androidTest scaffold for on-device integration
 * tests identified in TG-002. The recommended additions are:
 *
 *   - Room migration verification (versions 1 → 2 → 3 → 4 → 5)
 *   - EncryptedSharedPreferences round-trip (encrypt/decrypt a known value)
 *   - KeystoreManager encrypt/decrypt cycle using real Android Keystore
 *   - EdgeAgentForegroundService start/stop with a live Android context
 *
 * These tests require a physical device or emulator running API 31+ and cannot
 * run under Robolectric because they exercise the Android Keystore hardware and
 * the Room migration path with real SQLite.
 */
@RunWith(AndroidJUnit4::class)
class AndroidSmokeTest {

    /**
     * Sanity check: verifies the test instrumentation context is available and
     * the correct application package is under test.
     *
     * This test acts as a compile-time guard — if the androidTest directory is
     * present and the Gradle configuration is correct, this test will pass on
     * any device or emulator.
     */
    @Test
    fun applicationPackageIsCorrect() {
        val appContext = InstrumentationRegistry.getInstrumentation().targetContext
        assertEquals("com.fccmiddleware.edge", appContext.packageName)
    }
}

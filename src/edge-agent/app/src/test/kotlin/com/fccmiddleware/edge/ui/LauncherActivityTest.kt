package com.fccmiddleware.edge.ui

import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.sync.AgentCommandExecutor
import com.fccmiddleware.edge.ui.navigation.Routes
import io.mockk.every
import io.mockk.mockk
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.DisplayName
import org.junit.jupiter.api.Nested
import org.junit.jupiter.api.Test

/**
 * TG-001 — Unit tests for launcher routing logic.
 *
 * Tests the routing decision tree that determines which screen to show on startup:
 *   1. isDecommissioned=true  → decommissioned route
 *   2. isRegistered=true      → siteOverview route
 *   3. isRegistered=false     → provisioning route
 *   4. Exception reading prefs → provisioning route (safe fallback)
 *
 * The actual navigation is now handled by Compose NavHost; these tests verify
 * only the routing logic extracted as a pure function.
 */
class LauncherRoutingTest {

    private val encryptedPrefs: EncryptedPrefsManager = mockk(relaxed = true)
    private val agentCommandExecutor: AgentCommandExecutor = mockk(relaxed = true)

    /**
     * Pure routing function mirroring the logic in LauncherScreen composable.
     * Extracted here for testability without Compose/Activity dependencies.
     */
    private fun resolveRoute(): String {
        if (agentCommandExecutor.finalizeAckedResetIfNeeded("launcher")) {
            return Routes.PROVISIONING
        }
        return try {
            when {
                encryptedPrefs.isDecommissioned -> Routes.DECOMMISSIONED
                encryptedPrefs.isRegistered -> Routes.SITE_OVERVIEW
                else -> Routes.PROVISIONING
            }
        } catch (_: Exception) {
            Routes.PROVISIONING
        }
    }

    @Nested
    @DisplayName("Decommissioned path")
    inner class DecommissionedPath {
        @Test
        fun `routes to decommissioned when isDecommissioned is true`() {
            every { encryptedPrefs.isDecommissioned } returns true
            assertEquals(Routes.DECOMMISSIONED, resolveRoute())
        }
    }

    @Nested
    @DisplayName("Registered path")
    inner class RegisteredPath {
        @Test
        fun `routes to siteOverview when isRegistered is true`() {
            every { encryptedPrefs.isDecommissioned } returns false
            every { encryptedPrefs.isRegistered } returns true
            assertEquals(Routes.SITE_OVERVIEW, resolveRoute())
        }
    }

    @Nested
    @DisplayName("Not-registered path")
    inner class NotRegisteredPath {
        @Test
        fun `routes to provisioning when device is not registered`() {
            every { encryptedPrefs.isDecommissioned } returns false
            every { encryptedPrefs.isRegistered } returns false
            assertEquals(Routes.PROVISIONING, resolveRoute())
        }
    }

    @Nested
    @DisplayName("Exception-fallback path")
    inner class ExceptionFallbackPath {
        @Test
        fun `falls back to provisioning when isDecommissioned throws`() {
            every { encryptedPrefs.isDecommissioned } throws RuntimeException("Keystore corruption")
            assertEquals(Routes.PROVISIONING, resolveRoute())
        }

        @Test
        fun `falls back to provisioning when isRegistered throws`() {
            every { encryptedPrefs.isDecommissioned } returns false
            every { encryptedPrefs.isRegistered } throws IllegalStateException("EncryptedSharedPreferences not ready")
            assertEquals(Routes.PROVISIONING, resolveRoute())
        }
    }

    @Nested
    @DisplayName("Pending reset path")
    inner class PendingResetPath {
        @Test
        fun `routes to provisioning when pending reset finalized`() {
            every { agentCommandExecutor.finalizeAckedResetIfNeeded("launcher") } returns true
            assertEquals(Routes.PROVISIONING, resolveRoute())
        }
    }
}

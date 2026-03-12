package com.fccmiddleware.edge.config

import com.fccmiddleware.edge.buffer.dao.AgentConfigDao
import com.fccmiddleware.edge.buffer.entity.AgentConfig
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import io.mockk.slot
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * ConfigManagerTest — unit tests for EA-3.3 ConfigManager.
 *
 * Validates:
 *   - loadFromLocal() loads stored config into memory
 *   - loadFromLocal() handles missing config gracefully
 *   - applyConfig() rejects incompatible schema versions
 *   - applyConfig() skips older or equal configVersions
 *   - applyConfig() detects provisioning-only field changes
 *   - applyConfig() persists and applies valid new config
 *   - applyConfig() detects restart-required field changes
 *   - applyConfig() handles persistence failures
 *   - config StateFlow reflects applied config
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class ConfigManagerTest {

    private val agentConfigDao: AgentConfigDao = mockk(relaxed = true)
    private lateinit var configManager: ConfigManager

    companion object {
        private val BASE_CONFIG_JSON = canonicalEdgeConfigJson()
    }

    @Before
    fun setUp() {
        configManager = ConfigManager(agentConfigDao)
    }

    private fun parseConfig(jsonStr: String): EdgeAgentConfigDto =
        EdgeAgentConfigJson.decode(jsonStr)

    private fun configJsonWithVersion(version: Int): String =
        canonicalEdgeConfigJson(configVersion = version)

    // -------------------------------------------------------------------------
    // loadFromLocal
    // -------------------------------------------------------------------------

    @Test
    fun `loadFromLocal loads stored config into memory`() = runTest {
        coEvery { agentConfigDao.get() } returns AgentConfig(
            configJson = BASE_CONFIG_JSON,
            configVersion = 5,
            schemaVersion = 2,
            receivedAt = "2025-01-01T00:00:00Z",
        )

        configManager.loadFromLocal()

        val cfg = configManager.config.value
        assertNotNull(cfg)
        assertEquals(5, cfg!!.configVersion)
        assertEquals("SITE-001", cfg.identity.siteCode)
    }

    @Test
    fun `loadFromLocal handles missing config gracefully`() = runTest {
        coEvery { agentConfigDao.get() } returns null

        configManager.loadFromLocal()

        assertNull(configManager.config.value)
        assertNull(configManager.currentConfigVersion)
    }

    @Test
    fun `loadFromLocal handles corrupted JSON gracefully`() = runTest {
        coEvery { agentConfigDao.get() } returns AgentConfig(
            configJson = "{ not valid }",
            configVersion = 1,
            schemaVersion = 2,
            receivedAt = "2025-01-01T00:00:00Z",
        )

        configManager.loadFromLocal()

        assertNull(configManager.config.value)
    }

    // -------------------------------------------------------------------------
    // applyConfig — schema version checks
    // -------------------------------------------------------------------------

    @Test
    fun `rejects incompatible major schema version`() = runTest {
        val config = parseConfig(BASE_CONFIG_JSON).copy(schemaVersion = "3.0")

        val result = configManager.applyConfig(config, BASE_CONFIG_JSON)

        assertTrue(result is ConfigApplyResult.Rejected)
        assertEquals("INCOMPATIBLE_SCHEMA_VERSION", (result as ConfigApplyResult.Rejected).reason)
    }

    @Test
    fun `accepts same major version with different minor`() = runTest {
        val config = parseConfig(BASE_CONFIG_JSON).copy(schemaVersion = "1.1")

        val result = configManager.applyConfig(config, BASE_CONFIG_JSON)

        assertTrue(result is ConfigApplyResult.Applied)
    }

    @Test
    fun `rejects unsupported FCC protocol combination`() = runTest {
        val config = canonicalEdgeConfig(connectionProtocol = "REST")
        val invalidJson = EdgeAgentConfigJson.encode(config)

        val result = configManager.applyConfig(config, invalidJson)

        assertTrue(result is ConfigApplyResult.Rejected)
        assertEquals("UNSUPPORTED_FCC_CONFIGURATION", (result as ConfigApplyResult.Rejected).reason)
    }

    // -------------------------------------------------------------------------
    // applyConfig — version ordering
    // -------------------------------------------------------------------------

    @Test
    fun `skips config with same version as current`() = runTest {
        // First: apply version 5
        val config5 = parseConfig(BASE_CONFIG_JSON)
        configManager.applyConfig(config5, BASE_CONFIG_JSON)

        // Then: try to apply version 5 again
        val result = configManager.applyConfig(config5, BASE_CONFIG_JSON)

        assertTrue(result is ConfigApplyResult.Skipped)
    }

    @Test
    fun `skips config with older version than current`() = runTest {
        val config5 = parseConfig(BASE_CONFIG_JSON)
        configManager.applyConfig(config5, BASE_CONFIG_JSON)

        val olderJson = configJsonWithVersion(3)
        val config3 = parseConfig(olderJson)
        val result = configManager.applyConfig(config3, olderJson)

        assertTrue(result is ConfigApplyResult.Skipped)
    }

    @Test
    fun `applies config with newer version`() = runTest {
        val config5 = parseConfig(BASE_CONFIG_JSON)
        configManager.applyConfig(config5, BASE_CONFIG_JSON)

        val newerJson = configJsonWithVersion(6)
        val config6 = parseConfig(newerJson)
        val result = configManager.applyConfig(config6, newerJson)

        assertTrue(result is ConfigApplyResult.Applied)
        assertEquals(6, configManager.currentConfigVersion)
    }

    // -------------------------------------------------------------------------
    // applyConfig — provisioning-only fields
    // -------------------------------------------------------------------------

    @Test
    fun `rejects config with changed deviceId`() = runTest {
        val config5 = parseConfig(BASE_CONFIG_JSON)
        configManager.applyConfig(config5, BASE_CONFIG_JSON)

        val config6 = parseConfig(configJsonWithVersion(6)).copy(
            identity = config5.identity.copy(deviceId = "99999999-9999-9999-9999-999999999999"),
        )
        val result = configManager.applyConfig(config6, EdgeAgentConfigJson.encode(config6))

        assertTrue(result is ConfigApplyResult.Rejected)
        assertEquals("REPROVISION_REQUIRED", (result as ConfigApplyResult.Rejected).reason)
    }

    @Test
    fun `rejects config with changed siteCode`() = runTest {
        val config5 = parseConfig(BASE_CONFIG_JSON)
        configManager.applyConfig(config5, BASE_CONFIG_JSON)

        val config6 = parseConfig(configJsonWithVersion(6)).copy(
            identity = config5.identity.copy(siteCode = "SITE-999"),
        )
        val result = configManager.applyConfig(config6, EdgeAgentConfigJson.encode(config6))

        assertTrue(result is ConfigApplyResult.Rejected)
        assertEquals("REPROVISION_REQUIRED", (result as ConfigApplyResult.Rejected).reason)
    }

    @Test
    fun `rejects config with changed legalEntityId`() = runTest {
        val config5 = parseConfig(BASE_CONFIG_JSON)
        configManager.applyConfig(config5, BASE_CONFIG_JSON)

        val config6 = parseConfig(configJsonWithVersion(6)).copy(
            identity = config5.identity.copy(legalEntityId = "99999999-9999-9999-9999-999999999999"),
        )
        val result = configManager.applyConfig(config6, EdgeAgentConfigJson.encode(config6))

        assertTrue(result is ConfigApplyResult.Rejected)
        assertEquals("REPROVISION_REQUIRED", (result as ConfigApplyResult.Rejected).reason)
    }

    @Test
    fun `rejects config with changed localApiPort`() = runTest {
        val config5 = parseConfig(BASE_CONFIG_JSON)
        configManager.applyConfig(config5, BASE_CONFIG_JSON)

        val config6 = parseConfig(configJsonWithVersion(6)).copy(
            localApi = config5.localApi.copy(localhostPort = 9090),
        )
        val result = configManager.applyConfig(config6, EdgeAgentConfigJson.encode(config6))

        assertTrue(result is ConfigApplyResult.Rejected)
        assertEquals("REPROVISION_REQUIRED", (result as ConfigApplyResult.Rejected).reason)
    }

    // -------------------------------------------------------------------------
    // applyConfig — persistence
    // -------------------------------------------------------------------------

    @Test
    fun `persists config to Room on apply`() = runTest {
        val config5 = parseConfig(BASE_CONFIG_JSON)

        configManager.applyConfig(config5, BASE_CONFIG_JSON)

        val entitySlot = slot<AgentConfig>()
        coVerify { agentConfigDao.upsert(capture(entitySlot)) }
        assertEquals(5, entitySlot.captured.configVersion)
        assertEquals(BASE_CONFIG_JSON, entitySlot.captured.configJson)
        assertEquals(1, entitySlot.captured.id) // single-row table
    }

    @Test
    fun `persistence failure returns rejected`() = runTest {
        coEvery { agentConfigDao.upsert(any()) } throws RuntimeException("DB error")

        val config5 = parseConfig(BASE_CONFIG_JSON)
        val result = configManager.applyConfig(config5, BASE_CONFIG_JSON)

        assertTrue(result is ConfigApplyResult.Rejected)
        assertEquals("PERSISTENCE_FAILURE", (result as ConfigApplyResult.Rejected).reason)
    }

    // -------------------------------------------------------------------------
    // applyConfig — hot-reload fields update in-memory config
    // -------------------------------------------------------------------------

    @Test
    fun `hot-reload fields update in-memory config immediately`() = runTest {
        val config5 = parseConfig(BASE_CONFIG_JSON)
        configManager.applyConfig(config5, BASE_CONFIG_JSON)
        assertEquals(30, configManager.config.value!!.fcc.pullIntervalSeconds)

        // Apply version 6 with changed hot-reload field
        val config6 = canonicalEdgeConfig(configVersion = 6, pullIntervalSeconds = 60)
        val newerJson = EdgeAgentConfigJson.encode(config6)
        configManager.applyConfig(config6, newerJson)

        assertEquals(60, configManager.config.value!!.fcc.pullIntervalSeconds)
    }

    // -------------------------------------------------------------------------
    // First config apply (no existing config)
    // -------------------------------------------------------------------------

    @Test
    fun `first config apply skips provisioning checks`() = runTest {
        assertNull(configManager.config.value)

        val config5 = parseConfig(BASE_CONFIG_JSON)
        val result = configManager.applyConfig(config5, BASE_CONFIG_JSON)

        assertTrue(result is ConfigApplyResult.Applied)
        assertEquals(5, configManager.currentConfigVersion)
    }
}

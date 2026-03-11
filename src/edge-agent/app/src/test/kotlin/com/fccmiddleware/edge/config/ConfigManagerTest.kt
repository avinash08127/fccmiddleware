package com.fccmiddleware.edge.config

import com.fccmiddleware.edge.buffer.dao.AgentConfigDao
import com.fccmiddleware.edge.buffer.entity.AgentConfig
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import io.mockk.slot
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
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

    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
    }

    companion object {
        private val BASE_CONFIG_JSON = """
        {
            "schemaVersion": "2.0",
            "configVersion": 5,
            "configId": "00000000-0000-0000-0000-000000000001",
            "issuedAtUtc": "2025-01-01T00:00:00Z",
            "effectiveAtUtc": "2025-01-01T00:00:00Z",
            "compatibility": { "minAgentVersion": "1.0.0" },
            "agent": { "deviceId": "11111111-1111-1111-1111-111111111111", "isPrimaryAgent": true },
            "site": {
                "siteCode": "SITE-001",
                "legalEntityId": "22222222-2222-2222-2222-222222222222",
                "timezone": "Africa/Johannesburg",
                "currency": "ZAR",
                "operatingModel": "COCO",
                "connectivityMode": "CONNECTED"
            },
            "fccConnection": {
                "vendor": "DOMS",
                "host": "192.168.1.100",
                "port": 8080,
                "credentialsRef": "fcc/site-001",
                "protocolType": "REST",
                "transactionMode": "PULL",
                "ingestionMode": "RELAY",
                "heartbeatIntervalSeconds": 15
            },
            "polling": { "pullIntervalSeconds": 30, "batchSize": 100, "cursorStrategy": "LAST_SUCCESSFUL_TIMESTAMP" },
            "sync": {
                "cloudBaseUrl": "https://api.fccmiddleware.io",
                "uploadBatchSize": 50,
                "syncIntervalSeconds": 30,
                "statusPollIntervalSeconds": 30,
                "configPollIntervalSeconds": 60
            },
            "buffer": { "retentionDays": 30, "maxRecords": 50000, "cleanupIntervalHours": 24 },
            "api": { "localApiPort": 8585, "enableLanApi": false },
            "telemetry": { "telemetryIntervalSeconds": 60, "logLevel": "INFO" },
            "fiscalization": { "mode": "NONE", "requireCustomerTaxId": false, "fiscalReceiptRequired": false }
        }
        """.trimIndent()
    }

    @Before
    fun setUp() {
        configManager = ConfigManager(agentConfigDao)
    }

    private fun parseConfig(jsonStr: String): EdgeAgentConfigDto =
        json.decodeFromString<EdgeAgentConfigDto>(jsonStr)

    private fun configJsonWithVersion(version: Int): String =
        BASE_CONFIG_JSON.replace("\"configVersion\": 5", "\"configVersion\": $version")

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
        assertEquals("SITE-001", cfg.site.siteCode)
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
        val config = parseConfig(BASE_CONFIG_JSON).copy(schemaVersion = "2.1")

        val result = configManager.applyConfig(config, BASE_CONFIG_JSON)

        assertTrue(result is ConfigApplyResult.Applied)
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
            agent = config5.agent.copy(deviceId = "99999999-9999-9999-9999-999999999999"),
        )
        val result = configManager.applyConfig(config6, configJsonWithVersion(6))

        assertTrue(result is ConfigApplyResult.Rejected)
        assertEquals("REPROVISION_REQUIRED", (result as ConfigApplyResult.Rejected).reason)
    }

    @Test
    fun `rejects config with changed siteCode`() = runTest {
        val config5 = parseConfig(BASE_CONFIG_JSON)
        configManager.applyConfig(config5, BASE_CONFIG_JSON)

        val config6 = parseConfig(configJsonWithVersion(6)).copy(
            site = config5.site.copy(siteCode = "SITE-999"),
        )
        val result = configManager.applyConfig(config6, configJsonWithVersion(6))

        assertTrue(result is ConfigApplyResult.Rejected)
        assertEquals("REPROVISION_REQUIRED", (result as ConfigApplyResult.Rejected).reason)
    }

    @Test
    fun `rejects config with changed legalEntityId`() = runTest {
        val config5 = parseConfig(BASE_CONFIG_JSON)
        configManager.applyConfig(config5, BASE_CONFIG_JSON)

        val config6 = parseConfig(configJsonWithVersion(6)).copy(
            site = config5.site.copy(legalEntityId = "99999999-9999-9999-9999-999999999999"),
        )
        val result = configManager.applyConfig(config6, configJsonWithVersion(6))

        assertTrue(result is ConfigApplyResult.Rejected)
        assertEquals("REPROVISION_REQUIRED", (result as ConfigApplyResult.Rejected).reason)
    }

    @Test
    fun `rejects config with changed localApiPort`() = runTest {
        val config5 = parseConfig(BASE_CONFIG_JSON)
        configManager.applyConfig(config5, BASE_CONFIG_JSON)

        val config6 = parseConfig(configJsonWithVersion(6)).copy(
            api = config5.api.copy(localApiPort = 9090),
        )
        val result = configManager.applyConfig(config6, configJsonWithVersion(6))

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
        assertEquals(30, configManager.config.value!!.polling.pullIntervalSeconds)

        // Apply version 6 with changed hot-reload field
        val newerJson = configJsonWithVersion(6)
            .replace("\"pullIntervalSeconds\": 30", "\"pullIntervalSeconds\": 60")
        val config6 = parseConfig(newerJson)
        configManager.applyConfig(config6, newerJson)

        assertEquals(60, configManager.config.value!!.polling.pullIntervalSeconds)
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

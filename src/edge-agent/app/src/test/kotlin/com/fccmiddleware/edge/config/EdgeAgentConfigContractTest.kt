package com.fccmiddleware.edge.config

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class EdgeAgentConfigContractTest {

    @Test
    fun `canonical cloud config json deserializes on Android`() {
        val rawJson = canonicalEdgeConfigJson(configVersion = 9)

        val parsed = EdgeAgentConfigJson.decode(rawJson)

        assertEquals("1.0", parsed.schemaVersion)
        assertEquals(9, parsed.configVersion)
        assertEquals("SITE-001", parsed.identity.siteCode)
        assertEquals("ANDROID", parsed.identity.deviceClass)
        assertEquals("DOMS", parsed.fcc.vendor)
        assertEquals(8585, parsed.localApi.localhostPort)
        assertEquals(1, parsed.siteHa.leaderEpoch)
    }

    @Test
    fun `canonical config converts into live FCC runtime wiring`() {
        val config = canonicalEdgeConfig(
            configVersion = 9,
            connectionProtocol = "TCP",
            pullIntervalSeconds = 45,
        )

        val runtimeConfig = config.toAgentFccConfig()
        val localApiConfig = config.toLocalApiServerConfig()

        assertEquals("192.168.1.100", runtimeConfig.hostAddress)
        assertEquals(8080, runtimeConfig.port)
        assertEquals(45, runtimeConfig.pullIntervalSeconds)
        assertEquals("Africa/Johannesburg", runtimeConfig.timezone)
        assertEquals(8585, localApiConfig.port)
        assertTrue(config.requiresFccRuntime())
    }

    @Test
    fun `vendor specific adapter fields are projected into runtime config`() {
        val config = canonicalEdgeConfig(configVersion = 9).copy(
            fcc = canonicalEdgeConfig(configVersion = 9).fcc.copy(
                vendor = "ADVATEC",
                hostAddress = "10.20.30.40",
                advatecDevicePort = 5561,
                advatecWebhookToken = "adv-hook",
                advatecEfdSerialNumber = "SERIAL-1",
                advatecCustIdType = 2,
                advatecPumpMap = "{\"SERIAL-1\":4}",
                dppPorts = "2001,2002",
            ),
        )

        val runtimeConfig = config.toAgentFccConfig()

        assertEquals("10.20.30.40", runtimeConfig.advatecDeviceAddress)
        assertEquals(5561, runtimeConfig.advatecDevicePort)
        assertEquals("adv-hook", runtimeConfig.advatecWebhookToken)
        assertEquals("SERIAL-1", runtimeConfig.advatecEfdSerialNumber)
        assertEquals(2, runtimeConfig.advatecCustIdType)
        assertEquals("{\"SERIAL-1\":4}", runtimeConfig.advatecPumpMap)
        assertEquals("2001,2002", runtimeConfig.dppPorts)
    }

    @Test
    fun `peerDirectoryVersion is parsed from config JSON`() {
        val rawJson = canonicalEdgeConfigJson()

        val parsed = EdgeAgentConfigJson.decode(rawJson)

        assertEquals(1L, parsed.siteHa.peerDirectoryVersion)
    }

    @Test
    fun `LAN API remains localhost only until secure transport is implemented`() {
        val config = canonicalEdgeConfig(configVersion = 9).copy(
            localApi = LocalApiDto(
                localhostPort = 8585,
                enableLanApi = true,
                rateLimitPerMinute = 120,
            ),
        )

        val localApiConfig = config.toLocalApiServerConfig()

        assertTrue(localApiConfig.enableLanApi)
        assertEquals("127.0.0.1", localApiConfig.bindAddress)
        assertEquals(false, localApiConfig.lanExposureEnabled)
    }
}

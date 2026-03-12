package com.fccmiddleware.edge.adapter.common

import com.fccmiddleware.edge.adapter.doms.DomsJplAdapter
import com.fccmiddleware.edge.adapter.petronite.PetroniteAdapter
import com.fccmiddleware.edge.adapter.radix.RadixAdapter
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.runner.RunWith
import org.junit.Test
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class FccAdapterFactoryTest {

    private val factory = FccAdapterFactory()

    private fun makeConfig(vendor: FccVendor, protocol: String) = AgentFccConfig(
        fccVendor = vendor,
        connectionProtocol = protocol,
        hostAddress = "192.168.1.100",
        port = 8080,
        authCredential = "credential-ref",
        ingestionMode = IngestionMode.RELAY,
        pullIntervalSeconds = 30,
        productCodeMapping = emptyMap(),
        timezone = "Africa/Johannesburg",
        currencyCode = "ZAR",
    )

    @Test
    fun `resolve returns DOMS JPL adapter for TCP`() {
        val adapter = factory.resolve(FccVendor.DOMS, makeConfig(FccVendor.DOMS, "TCP"))

        assertTrue(adapter is DomsJplAdapter)
    }

    @Test
    fun `resolve rejects DOMS REST`() {
        assertThrows(AdapterNotImplementedException::class.java) {
            factory.resolve(FccVendor.DOMS, makeConfig(FccVendor.DOMS, "REST"))
        }
    }

    @Test
    fun `resolve returns Radix adapter`() {
        val adapter = factory.resolve(FccVendor.RADIX, makeConfig(FccVendor.RADIX, "REST"))

        assertTrue(adapter is RadixAdapter)
    }

    @Test
    fun `resolve returns Petronite adapter`() {
        val adapter = factory.resolve(FccVendor.PETRONITE, makeConfig(FccVendor.PETRONITE, "REST"))

        assertTrue(adapter is PetroniteAdapter)
    }

    @Test
    fun `resolve rejects Advatec`() {
        assertThrows(AdapterNotImplementedException::class.java) {
            factory.resolve(FccVendor.ADVATEC, makeConfig(FccVendor.ADVATEC, "REST"))
        }
    }
}

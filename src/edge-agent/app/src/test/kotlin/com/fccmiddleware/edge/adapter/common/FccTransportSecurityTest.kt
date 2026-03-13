package com.fccmiddleware.edge.adapter.common

import org.junit.Assert.assertEquals
import org.junit.Test

class FccTransportSecurityTest {

    @Test
    fun `bare non-loopback host defaults to https`() {
        val endpoint = FccTransportSecurity.resolveEndpoint(
            rawAddress = "petronite.example",
            component = "PetroniteAdapter",
            defaultPort = 443,
        )

        assertEquals("https", endpoint.scheme)
        assertEquals("petronite.example", endpoint.host)
        assertEquals(443, endpoint.port)
        assertEquals("https://petronite.example:443", endpoint.asBaseUrl())
    }

    @Test
    fun `bare loopback host defaults to http`() {
        val endpoint = FccTransportSecurity.resolveEndpoint(
            rawAddress = "127.0.0.1",
            component = "AdvatecAdapter",
            defaultPort = 5560,
        )

        assertEquals("http", endpoint.scheme)
        assertEquals("127.0.0.1", endpoint.host)
        assertEquals(5560, endpoint.port)
        assertEquals("http://127.0.0.1:5560/api/v2/incoming", endpoint.resolve("/api/v2/incoming"))
    }

    @Test(expected = IllegalArgumentException::class)
    fun `explicit cleartext http is rejected for non-loopback FCC endpoints`() {
        FccTransportSecurity.resolveEndpoint(
            rawAddress = "http://192.168.1.25",
            component = "PetroniteAdapter",
            defaultPort = 443,
        )
    }

    @Test
    fun `absolute https urls preserve path and query`() {
        val url = FccTransportSecurity.resolveAbsoluteUrl(
            rawUrl = "https://petronite.example:8443/oauth/token?grant_type=client_credentials",
            component = "PetroniteOAuth",
        )

        assertEquals(
            "https://petronite.example:8443/oauth/token?grant_type=client_credentials",
            url,
        )
    }
}

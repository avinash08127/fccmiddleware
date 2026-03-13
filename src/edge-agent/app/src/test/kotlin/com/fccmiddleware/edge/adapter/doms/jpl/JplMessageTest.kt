package com.fccmiddleware.edge.adapter.doms.jpl

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class JplMessageTest {

    @Test
    fun `toString redacts access code values`() {
        val message = JplMessage(
            name = "FcLogon_req",
            data = mapOf(
                "FcAccessCode" to "POS,RI,APPL_ID=10",
                "CountryCode" to "TZ",
            ),
        )

        val rendered = message.toString()

        assertTrue(rendered.contains("FcAccessCode=[REDACTED]"))
        assertTrue(rendered.contains("CountryCode=TZ"))
        assertFalse(rendered.contains("POS,RI,APPL_ID=10"))
    }
}

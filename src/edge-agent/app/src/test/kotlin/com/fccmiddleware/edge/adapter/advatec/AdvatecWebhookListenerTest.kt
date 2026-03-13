package com.fccmiddleware.edge.adapter.advatec

import android.util.Log
import io.mockk.every
import io.mockk.mockkStatic
import io.mockk.unmockkStatic
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.net.HttpURLConnection
import java.net.ServerSocket
import java.net.Socket
import java.net.URL

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class AdvatecWebhookListenerTest {

    @Before
    fun setUp() {
        mockkStatic(Log::class)
        every { Log.d(any(), any()) } returns 0
        every { Log.i(any(), any()) } returns 0
        every { Log.w(any(), any<String>()) } returns 0
        every { Log.e(any(), any<String>()) } returns 0
        every { Log.e(any(), any<String>(), any()) } returns 0
    }

    @After
    fun tearDown() {
        unmockkStatic(Log::class)
    }

    @Test
    fun `valid webhook token returns 200 and enqueues payload`() {
        withListener(webhookToken = "expected-token") { listener, port ->
            val response = postWebhook(port, """{"DataType":"Receipt"}""", token = "expected-token")

            assertEquals(200, response.statusCode)
            assertEquals("OK", response.body)
            assertEquals(1, listener.queueSize)
        }
    }

    @Test
    fun `missing or invalid webhook token returns 401 and does not enqueue payload`() {
        withListener(webhookToken = "expected-token") { listener, port ->
            val response = postWebhook(port, """{"DataType":"Receipt"}""", token = "wrong-token")

            assertEquals(401, response.statusCode)
            assertEquals("Unauthorized", response.body)
            assertEquals(0, listener.queueSize)
        }
    }

    @Test
    fun `empty request body returns 400`() {
        withListener(webhookToken = "expected-token") { listener, port ->
            val response = postWebhook(port, "", token = "expected-token")

            assertEquals(400, response.statusCode)
            assertEquals("Bad Request", response.body)
            assertEquals(0, listener.queueSize)
        }
    }

    private fun withListener(
        webhookToken: String?,
        block: (AdvatecWebhookListener, Int) -> Unit,
    ) {
        val port = ServerSocket(0).use { it.localPort }
        val listener = AdvatecWebhookListener(
            listenPort = port,
            siteCode = "SITE-A",
            webhookToken = webhookToken,
        )

        assertTrue(listener.start())
        waitForListener(port)

        try {
            block(listener, port)
        } finally {
            listener.stop()
        }
    }

    private fun waitForListener(port: Int) {
        repeat(20) {
            try {
                Socket("127.0.0.1", port).use { return }
            } catch (_: Exception) {
                Thread.sleep(50)
            }
        }
        throw AssertionError("Listener failed to start on port $port")
    }

    private fun postWebhook(port: Int, body: String, token: String?): HttpResponse {
        val connection = (URL("http://127.0.0.1:$port/api/webhook/advatec").openConnection() as HttpURLConnection).apply {
            requestMethod = "POST"
            doOutput = true
            setRequestProperty("Content-Type", "application/json")
            if (token != null) {
                setRequestProperty("X-Webhook-Token", token)
            }
        }

        if (body.isNotEmpty()) {
            connection.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
        } else {
            connection.setFixedLengthStreamingMode(0)
            connection.outputStream.close()
        }

        val statusCode = connection.responseCode
        val responseBody = if (statusCode in 200..299) {
            connection.inputStream.bufferedReader().readText()
        } else {
            connection.errorStream?.bufferedReader()?.readText().orEmpty()
        }
        connection.disconnect()

        return HttpResponse(statusCode = statusCode, body = responseBody)
    }

    private data class HttpResponse(
        val statusCode: Int,
        val body: String,
    )
}

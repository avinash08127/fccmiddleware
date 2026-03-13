package com.fccmiddleware.edge.provisioning

import com.fccmiddleware.edge.sync.CloudErrorResponse
import com.fccmiddleware.edge.sync.CloudRegistrationResult
import com.fccmiddleware.edge.sync.CloudTokenRefreshResult
import com.fccmiddleware.edge.sync.DeviceRegistrationRequest
import com.fccmiddleware.edge.sync.DeviceRegistrationResponse
import com.fccmiddleware.edge.sync.HttpCloudApiClient
import com.fccmiddleware.edge.sync.TokenRefreshResponse
import io.ktor.client.HttpClient
import io.ktor.client.engine.mock.MockEngine
import io.ktor.client.engine.mock.respond
import io.ktor.client.plugins.contentnegotiation.ContentNegotiation
import io.ktor.http.ContentType
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpStatusCode
import io.ktor.http.headersOf
import io.ktor.serialization.kotlinx.json.json
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.DisplayName
import org.junit.jupiter.api.Nested
import org.junit.jupiter.api.Test

class RegistrationApiTest {

    private val jsonParser = Json {
        ignoreUnknownKeys = true
        isLenient = true
    }

    private fun createClient(mockEngine: MockEngine): HttpCloudApiClient {
        val httpClient = HttpClient(mockEngine) {
            install(ContentNegotiation) {
                json(jsonParser)
            }
        }
        return HttpCloudApiClient("https://api.test.io", httpClient)
    }

    private val jsonHeaders = headersOf(HttpHeaders.ContentType, ContentType.Application.Json.toString())

    @Nested
    @DisplayName("registerDevice")
    inner class RegisterDevice {

        private val sampleRequest = DeviceRegistrationRequest(
            provisioningToken = "test-token",
            siteCode = "MW-LLW-001",
            deviceSerialNumber = "SN12345",
            deviceModel = "i9100",
            osVersion = "12",
            agentVersion = "1.0.0",
        )

        @Test
        fun `returns Success on HTTP 201`() = runTest {
            val response = DeviceRegistrationResponse(
                deviceId = "d1234567-0000-0000-0000-000000000001",
                deviceToken = "eyJhbGciOiJFUzI1NiJ9.test",
                refreshToken = "opaque-refresh-token",
                tokenExpiresAt = "2026-03-12T00:00:00Z",
                siteCode = "MW-LLW-001",
                legalEntityId = "10000000-0000-0000-0000-000000000001",
                siteConfig = null,
                registeredAt = "2026-03-11T12:00:00Z",
            )

            val engine = MockEngine {
                respond(
                    content = jsonParser.encodeToString(response),
                    status = HttpStatusCode.Created,
                    headers = jsonHeaders,
                )
            }

            val client = createClient(engine)
            val result = client.registerDevice("https://api.test.io", sampleRequest)

            assertTrue(result is CloudRegistrationResult.Success)
            val success = result as CloudRegistrationResult.Success
            assertEquals("d1234567-0000-0000-0000-000000000001", success.response.deviceId)
            assertEquals("MW-LLW-001", success.response.siteCode)
            assertEquals("opaque-refresh-token", success.response.refreshToken)
        }

        @Test
        fun `returns Rejected on HTTP 400`() = runTest {
            val error = CloudErrorResponse("TOKEN_EXPIRED", "Provisioning token has expired")

            val engine = MockEngine {
                respond(
                    content = jsonParser.encodeToString(error),
                    status = HttpStatusCode.BadRequest,
                    headers = jsonHeaders,
                )
            }

            val client = createClient(engine)
            val result = client.registerDevice("https://api.test.io", sampleRequest)

            assertTrue(result is CloudRegistrationResult.Rejected)
            val rejected = result as CloudRegistrationResult.Rejected
            assertEquals("TOKEN_EXPIRED", rejected.errorCode)
        }

        @Test
        fun `returns Rejected on HTTP 409 conflict`() = runTest {
            val error = CloudErrorResponse("TOKEN_ALREADY_USED", "Token has already been used")

            val engine = MockEngine {
                respond(
                    content = jsonParser.encodeToString(error),
                    status = HttpStatusCode.Conflict,
                    headers = jsonHeaders,
                )
            }

            val client = createClient(engine)
            val result = client.registerDevice("https://api.test.io", sampleRequest)

            assertTrue(result is CloudRegistrationResult.Rejected)
            assertEquals("TOKEN_ALREADY_USED", (result as CloudRegistrationResult.Rejected).errorCode)
        }

        @Test
        fun `returns TransportError on HTTP 500`() = runTest {
            val engine = MockEngine {
                respond(
                    content = "Internal Server Error",
                    status = HttpStatusCode.InternalServerError,
                )
            }

            val client = createClient(engine)
            val result = client.registerDevice("https://api.test.io", sampleRequest)

            assertTrue(result is CloudRegistrationResult.TransportError)
        }

        @Test
        fun `returns TransportError on network failure`() = runTest {
            val engine = MockEngine {
                throw java.io.IOException("Connection refused")
            }

            val client = createClient(engine)
            val result = client.registerDevice("https://api.test.io", sampleRequest)

            assertTrue(result is CloudRegistrationResult.TransportError)
            assertTrue((result as CloudRegistrationResult.TransportError).message.contains("Connection refused"))
        }

        @Test
        fun `uses dedicated pinned registration client when bootstrap pins are present`() = runTest {
            var recordedBaseUrl: String? = null
            var recordedPins: List<String>? = null

            val baseClient = HttpClient(
                MockEngine {
                    error("un-pinned pre-registration client should not be used for registerDevice")
                },
            ) {
                install(ContentNegotiation) {
                    json(jsonParser)
                }
            }

            val response = DeviceRegistrationResponse(
                deviceId = "d1234567-0000-0000-0000-000000000001",
                deviceToken = "eyJhbGciOiJFUzI1NiJ9.test",
                refreshToken = "opaque-refresh-token",
                tokenExpiresAt = "2026-03-12T00:00:00Z",
                siteCode = "MW-LLW-001",
                legalEntityId = "10000000-0000-0000-0000-000000000001",
                siteConfig = null,
                registeredAt = "2026-03-11T12:00:00Z",
            )
            val pinnedClient = HttpClient(
                MockEngine {
                    respond(
                        content = jsonParser.encodeToString(response),
                        status = HttpStatusCode.Created,
                        headers = jsonHeaders,
                    )
                },
            ) {
                install(ContentNegotiation) {
                    json(jsonParser)
                }
            }

            val client = HttpCloudApiClient(
                cloudBaseUrl = HttpCloudApiClient.PRE_REGISTRATION_URL,
                httpClient = baseClient,
                certificatePins = listOf("sha256/test-bootstrap-pin"),
                registrationClientFactory = { baseUrl, pins, _ ->
                    recordedBaseUrl = baseUrl
                    recordedPins = pins
                    pinnedClient
                },
            )

            val result = client.registerDevice("https://api.test.io", sampleRequest)

            assertTrue(result is CloudRegistrationResult.Success)
            assertEquals("https://api.test.io", recordedBaseUrl)
            assertEquals(listOf("sha256/test-bootstrap-pin"), recordedPins)
        }
    }

    @Nested
    @DisplayName("refreshToken")
    inner class RefreshToken {

        @Test
        fun `returns Success with new tokens on HTTP 200`() = runTest {
            val response = TokenRefreshResponse(
                deviceToken = "new-jwt-token",
                refreshToken = "new-refresh-token",
                tokenExpiresAt = "2026-03-12T12:00:00Z",
            )

            val engine = MockEngine {
                respond(
                    content = jsonParser.encodeToString(response),
                    status = HttpStatusCode.OK,
                    headers = jsonHeaders,
                )
            }

            val client = createClient(engine)
            val result = client.refreshToken("old-refresh-token", "expired-device-jwt")

            assertTrue(result is CloudTokenRefreshResult.Success)
            val success = result as CloudTokenRefreshResult.Success
            assertEquals("new-jwt-token", success.response.deviceToken)
            assertEquals("new-refresh-token", success.response.refreshToken)
        }

        @Test
        fun `returns Unauthorized on HTTP 401`() = runTest {
            val engine = MockEngine {
                respond(
                    content = "",
                    status = HttpStatusCode.Unauthorized,
                )
            }

            val client = createClient(engine)
            val result = client.refreshToken("expired-refresh-token", "expired-device-jwt")

            assertTrue(result is CloudTokenRefreshResult.Unauthorized)
        }

        @Test
        fun `returns Forbidden with error code on HTTP 403`() = runTest {
            val error = CloudErrorResponse("DEVICE_DECOMMISSIONED", "Device has been decommissioned")

            val engine = MockEngine {
                respond(
                    content = jsonParser.encodeToString(error),
                    status = HttpStatusCode.Forbidden,
                    headers = jsonHeaders,
                )
            }

            val client = createClient(engine)
            val result = client.refreshToken("some-refresh-token", "expired-device-jwt")

            assertTrue(result is CloudTokenRefreshResult.Forbidden)
            assertEquals("DEVICE_DECOMMISSIONED", (result as CloudTokenRefreshResult.Forbidden).errorCode)
        }

        @Test
        fun `returns TransportError on network failure`() = runTest {
            val engine = MockEngine {
                throw java.io.IOException("Timeout")
            }

            val client = createClient(engine)
            val result = client.refreshToken("refresh-token", "expired-device-jwt")

            assertTrue(result is CloudTokenRefreshResult.TransportError)
        }
    }
}

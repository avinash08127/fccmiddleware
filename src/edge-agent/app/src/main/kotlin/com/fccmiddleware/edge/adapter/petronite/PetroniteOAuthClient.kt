package com.fccmiddleware.edge.adapter.petronite

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import io.ktor.client.HttpClient
import io.ktor.client.request.header
import io.ktor.client.request.post
import io.ktor.client.request.setBody
import io.ktor.client.statement.bodyAsText
import io.ktor.http.ContentType
import io.ktor.http.HttpStatusCode
import io.ktor.http.contentType
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.json.Json

/**
 * OAuth2 Client Credentials client for the Petronite API.
 *
 * POST /oauth/token with Basic auth header (Base64(clientId:clientSecret)),
 * Content-Type: application/x-www-form-urlencoded, body: grant_type=client_credentials.
 *
 * Caches the access token using the server-supplied `expires_in` TTL.
 * Proactively refreshes 60 seconds before expiry. Thread-safe via [Mutex].
 * Exposes [invalidateToken] for 401 retry patterns.
 */
class PetroniteOAuthClient(
    private val config: AgentFccConfig,
    private val httpClient: HttpClient,
    private val json: Json = Json { ignoreUnknownKeys = true },
) {

    private val mutex = Mutex()

    @Volatile
    private var cachedToken: String? = null

    @Volatile
    private var tokenExpiresAtMillis: Long = 0L

    /**
     * Returns a valid access token, fetching or refreshing as needed.
     * Thread-safe: concurrent callers will wait on the mutex while one caller refreshes.
     */
    suspend fun getAccessToken(): String {
        // Fast path: token is still valid (with proactive buffer).
        val token = cachedToken
        if (token != null && System.currentTimeMillis() < tokenExpiresAtMillis - PROACTIVE_REFRESH_BUFFER_MS) {
            return token
        }

        mutex.withLock {
            // Double-check after acquiring the lock -- another coroutine may have refreshed.
            val rechecked = cachedToken
            if (rechecked != null && System.currentTimeMillis() < tokenExpiresAtMillis - PROACTIVE_REFRESH_BUFFER_MS) {
                return rechecked
            }

            val response = requestToken()
            cachedToken = response.accessToken
            tokenExpiresAtMillis = System.currentTimeMillis() + (response.expiresIn * 1000L)

            AppLogger.d(TAG, "OAuth token acquired (expires in ${response.expiresIn}s)")
            return response.accessToken
        }
    }

    /**
     * Invalidates the cached token so the next call to [getAccessToken]
     * will force a fresh token request. Use after receiving a 401 from the Petronite API.
     */
    suspend fun invalidateToken() {
        mutex.withLock {
            cachedToken = null
            tokenExpiresAtMillis = 0L
            AppLogger.d(TAG, "OAuth token invalidated")
        }
    }

    // -- Private helpers ------------------------------------------------------

    private suspend fun requestToken(): PetroniteTokenResponse {
        val tokenEndpoint = config.oauthTokenEndpoint
            ?: throw IllegalStateException("Petronite OAuth token endpoint is not configured")
        val clientId = config.clientId
            ?: throw IllegalStateException("Petronite OAuth client ID is not configured")
        val clientSecret = config.clientSecret
            ?: throw IllegalStateException("Petronite OAuth client secret is not configured")

        // Basic auth: Base64(clientId:clientSecret)
        val credentials = android.util.Base64.encodeToString(
            "$clientId:$clientSecret".toByteArray(Charsets.UTF_8),
            android.util.Base64.NO_WRAP,
        )

        try {
            val response = httpClient.post(tokenEndpoint) {
                header("Authorization", "Basic $credentials")
                contentType(ContentType.Application.FormUrlEncoded)
                setBody("grant_type=client_credentials")
            }

            val body = response.bodyAsText()

            if (response.status != HttpStatusCode.OK) {
                val statusCode = response.status.value
                AppLogger.w(TAG, "OAuth token request failed: HTTP $statusCode -- $body")
                throw PetroniteHttpException(
                    message = "Petronite OAuth token request returned HTTP $statusCode",
                    statusCode = statusCode,
                )
            }

            val tokenResponse = json.decodeFromString<PetroniteTokenResponse>(body)

            if (tokenResponse.accessToken.isBlank()) {
                throw IllegalStateException("Petronite OAuth token response has blank access_token")
            }

            return tokenResponse
        } catch (e: CancellationException) {
            throw e
        } catch (e: PetroniteHttpException) {
            throw e
        } catch (e: IllegalStateException) {
            throw e
        } catch (e: Exception) {
            throw PetroniteHttpException(
                message = "Petronite OAuth token request transport failure: ${e::class.simpleName}: ${e.message}",
                statusCode = 0,
                cause = e,
            )
        }
    }

    companion object {
        private const val TAG = "PetroniteOAuth"

        /** Proactive refresh buffer: refresh token 60 seconds before expiry. */
        private const val PROACTIVE_REFRESH_BUFFER_MS = 60_000L
    }
}

/**
 * Exception for Petronite HTTP errors.
 * Carries the HTTP status code for caller handling (401 retry, etc.).
 */
class PetroniteHttpException(
    message: String,
    val statusCode: Int,
    cause: Throwable? = null,
) : Exception(message, cause)

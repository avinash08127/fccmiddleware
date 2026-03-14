package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.config.CloudEnvironments
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import io.ktor.client.HttpClient
import kotlinx.coroutines.CancellationException
import java.util.UUID
import io.ktor.client.call.body
import io.ktor.client.engine.okhttp.OkHttp
import io.ktor.client.plugins.contentnegotiation.ContentNegotiation
import io.ktor.client.request.bearerAuth
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.request.parameter
import io.ktor.client.request.post
import io.ktor.client.request.setBody
import io.ktor.client.statement.bodyAsText
import io.ktor.http.ContentType
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpStatusCode
import io.ktor.http.contentType
import io.ktor.serialization.kotlinx.json.json
import kotlinx.serialization.json.Json
import okhttp3.CertificatePinner
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

// ---------------------------------------------------------------------------
// Status poll result type
// ---------------------------------------------------------------------------

sealed class CloudStatusPollResult {
    /** HTTP 200 — FCC transaction IDs returned for the requested since watermark. */
    data class Success(val response: SyncedStatusResponse) : CloudStatusPollResult()

    /** HTTP 401 — access token expired or invalid; caller should refresh and retry. */
    data object Unauthorized : CloudStatusPollResult()

    /**
     * HTTP 403 — access forbidden.
     * [errorCode] == "DEVICE_DECOMMISSIONED" means the device has been deactivated.
     */
    data class Forbidden(val errorCode: String?) : CloudStatusPollResult()

    /** M-15: HTTP 429 — rate limited by cloud. */
    data class RateLimited(val retryAfterSeconds: Long?) : CloudStatusPollResult()

    /** Network or non-2xx/401/403/429 failure. Retry on next cadence tick. */
    data class TransportError(val message: String) : CloudStatusPollResult()
}

// ---------------------------------------------------------------------------
// Upload result type
// ---------------------------------------------------------------------------

sealed class CloudUploadResult {
    /** HTTP 200 — batch processed. Check per-record outcomes in [response]. */
    data class Success(val response: CloudUploadResponse) : CloudUploadResult()

    /** HTTP 401 — access token expired or invalid; caller should refresh and retry. */
    data object Unauthorized : CloudUploadResult()

    /**
     * HTTP 403 — access forbidden.
     * [errorCode] == "DEVICE_DECOMMISSIONED" means the device has been deactivated;
     * all sync must stop permanently.
     */
    data class Forbidden(val errorCode: String?) : CloudUploadResult()

    /**
     * M-15: HTTP 429 — rate limited by cloud. Caller should back off for
     * [retryAfterSeconds] seconds (from Retry-After header), or use default backoff if null.
     */
    data class RateLimited(val retryAfterSeconds: Long?) : CloudUploadResult()

    /**
     * M-15: HTTP 413 — request payload too large. Caller should reduce batch size and retry.
     */
    data object PayloadTooLarge : CloudUploadResult()

    /** Network or non-2xx/401/403/429/413 failure. Retry on next cadence tick. */
    data class TransportError(val message: String) : CloudUploadResult()
}

// ---------------------------------------------------------------------------
// Diagnostic log upload result type
// ---------------------------------------------------------------------------

sealed class CloudDiagnosticLogResult {
    data object Success : CloudDiagnosticLogResult()
    data object Unauthorized : CloudDiagnosticLogResult()
    data class Forbidden(val errorCode: String?) : CloudDiagnosticLogResult()
    data class TransportError(val message: String) : CloudDiagnosticLogResult()
}

// ---------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------

/**
 * Abstraction over the cloud HTTP API for the upload worker.
 *
 * A mock implementation is used in unit tests; [HttpCloudApiClient] is used in production.
 */
interface CloudApiClient {

    /**
     * Upload a batch of transactions to POST /api/v1/transactions/upload.
     *
     * @param request Batch request with transactions in ascending completedAt order.
     * @param bearerToken Device JWT from [DeviceTokenProvider.getAccessToken].
     */
    suspend fun uploadBatch(
        request: CloudUploadRequest,
        bearerToken: String,
    ): CloudUploadResult

    /**
     * Poll cloud for synced-to-Odoo status.
     *
     * Calls `GET /api/v1/transactions/synced-status?since={iso-8601-utc}`.
     *
     * @param since Inclusive UTC lower bound for SYNCED_TO_ODOO acknowledgements.
     * @param bearerToken Device JWT from [DeviceTokenProvider.getAccessToken].
     */
    suspend fun getSyncedStatus(
        since: String,
        bearerToken: String,
    ): CloudStatusPollResult

    /**
     * Poll cloud for agent configuration updates.
     *
     * Calls `GET /api/v1/agent/config` with `If-None-Match: {currentConfigVersion}`.
     * Returns [CloudConfigPollResult.NotModified] on HTTP 304.
     *
     * @param currentConfigVersion Current config version for ETag (null = first poll).
     * @param bearerToken Device JWT from [DeviceTokenProvider.getAccessToken].
     */
    suspend fun getConfig(
        currentConfigVersion: Int?,
        bearerToken: String,
    ): CloudConfigPollResult

    /**
     * Submit telemetry payload to POST /api/v1/agent/telemetry.
     *
     * Returns [CloudTelemetryResult.Success] on HTTP 204.
     * Idempotent by (deviceId, sequenceNumber) — duplicates silently discarded.
     *
     * @param payload Full telemetry snapshot.
     * @param bearerToken Device JWT from [DeviceTokenProvider.getAccessToken].
     */
    suspend fun submitTelemetry(
        payload: TelemetryPayload,
        bearerToken: String,
    ): CloudTelemetryResult

    /**
     * Forward a pre-auth record to the cloud for tracking.
     *
     * Calls `POST /api/v1/preauth` with the pre-auth details.
     * Dedup key: (odooOrderId, siteCode). Re-posting with the same key and
     * an updated status triggers a status transition on the cloud record.
     *
     * @param request Pre-auth forward request.
     * @param bearerToken Device JWT from [DeviceTokenProvider.getAccessToken].
     */
    suspend fun forwardPreAuth(
        request: PreAuthForwardRequest,
        bearerToken: String,
    ): CloudPreAuthForwardResult

    /**
     * Register the device with the cloud.
     *
     * Calls `POST /api/v1/agent/register` with bootstrap token and device fingerprint.
     * No bearer token required — authentication is via the one-time provisioning token.
     *
     * @param cloudBaseUrl Cloud API base URL from QR code (may differ from the
     *   configured base URL since the client may not yet be configured).
     * @param request Registration request with bootstrap data.
     */
    suspend fun registerDevice(
        cloudBaseUrl: String,
        request: DeviceRegistrationRequest,
    ): CloudRegistrationResult

    /**
     * Refresh the device access token.
     *
     * Calls `POST /api/v1/agent/token/refresh` with the current refresh token
     * and the current (even expired) device JWT.
     *
     * @param refreshToken Current opaque refresh token.
     * @param deviceToken Current (possibly expired) device JWT for identity binding (FM-S03).
     */
    suspend fun refreshToken(refreshToken: String, deviceToken: String): CloudTokenRefreshResult

    /**
     * Poll cloud for pending agent-control commands.
     *
     * Calls `GET /api/v1/agent/commands`.
     */
    suspend fun pollCommands(bearerToken: String): CloudCommandPollResult

    /**
     * Acknowledge a previously fetched command.
     *
     * Calls `POST /api/v1/agent/commands/{commandId}/ack`.
     */
    suspend fun ackCommand(
        commandId: String,
        request: CommandAckRequest,
        bearerToken: String,
    ): CloudCommandAckResult

    /**
     * Upsert the current Android FCM installation token for the authenticated device.
     *
     * Calls `POST /api/v1/agent/installations/android`.
     */
    suspend fun upsertAndroidInstallation(
        request: AndroidInstallationUpsertRequest,
        bearerToken: String,
    ): CloudInstallationUpsertResult

    /**
     * Check agent version compatibility with the cloud.
     *
     * Calls `GET /api/v1/agent/version-check?agentVersion={version}`.
     * Returns compatibility info including whether the agent must be updated.
     *
     * @param agentVersion Agent version in semantic format (e.g. "1.0.0").
     * @param bearerToken Device JWT from [DeviceTokenProvider.getAccessToken].
     */
    suspend fun checkVersion(
        agentVersion: String,
        bearerToken: String,
    ): CloudVersionCheckResult

    /**
     * Upload diagnostic log entries (WARN/ERROR) to POST /api/v1/agent/diagnostic-logs.
     *
     * Only called when config `telemetry.includeDiagnosticsLogs` is true.
     * Max 200 entries per batch. Fire-and-forget: failures are silently discarded.
     */
    suspend fun submitDiagnosticLogs(
        request: DiagnosticLogUploadRequest,
        bearerToken: String,
    ): CloudDiagnosticLogResult

    /**
     * AP-029: Lightweight connectivity probe — GET /health on the cloud host.
     *
     * Shares the existing Ktor/OkHttp connection pool with all other API calls,
     * eliminating the separate OkHttpClient and its redundant TCP connection.
     * Returns true on HTTP 2xx, false on any error or non-success status.
     */
    suspend fun healthCheck(): Boolean

    /**
     * Update the cloud base URL at runtime.
     *
     * Called after device registration to replace the stub "not-yet-provisioned" URL
     * with the real cloud endpoint, avoiding the need to restart the process or
     * recreate the DI graph.
     */
    fun updateBaseUrl(newBaseUrl: String)
}

// ---------------------------------------------------------------------------
// Ktor / OkHttp implementation
// ---------------------------------------------------------------------------

/**
 * Production [CloudApiClient] backed by Ktor with the OkHttp engine.
 *
 * Thread-safe; the underlying [HttpClient] is stateless per call.
 *
 * @param cloudBaseUrl Base URL of the cloud API, e.g. "https://api.fccmiddleware.io".
 *   Trailing slash must be absent.
 * @param httpClient Ktor [HttpClient] configured with JSON content negotiation.
 *   Use [create] factory for the standard production setup.
 */
class HttpCloudApiClient(
    @Volatile private var cloudBaseUrl: String,
    @Volatile private var httpClient: HttpClient,
    private val encryptedPrefsManager: EncryptedPrefsManager? = null,
    private val certificatePins: List<String> = emptyList(),
    private val socketFactory: javax.net.SocketFactory? = null,
    private val registrationClientFactory: ((String, List<String>, javax.net.SocketFactory?) -> HttpClient)? = null,
) : CloudApiClient {

    /** NET-007: Guards atomic update of cloudBaseUrl + httpClient in updateBaseUrl(). */
    private val urlUpdateLock = Any()

    init {
        cloudBaseUrl = resolveBaseUrl(cloudBaseUrl)
    }

    /**
     * Resolve the effective base URL. If an environment key is stored in
     * [EncryptedPrefsManager], resolve from [CloudEnvironments]; otherwise
     * fall back to the explicit URL.
     */
    private fun resolveBaseUrl(explicitUrl: String): String {
        val env = encryptedPrefsManager?.environment
        val resolved = if (env != null) CloudEnvironments.resolve(env) else null
        if (resolved != null) {
            AppLogger.i(TAG, "Resolved base URL from environment '$env'")
            return resolved.trimEnd('/')
        }
        return explicitUrl.trimEnd('/')
    }

    override suspend fun healthCheck(): Boolean {
        return try {
            val response = httpClient.get("$cloudBaseUrl/health")
            // NET-014: Explicitly consume the response body so the underlying
            // OkHttp connection is returned to the pool and can be reused on
            // subsequent probes (avoids a fresh TCP+TLS handshake every 30s).
            response.bodyAsText()
            response.status == HttpStatusCode.OK
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            false
        }
    }

    /**
     * NET-015: Try to parse the cloud error body for the backend's `retryable` hint
     * and build an enhanced transport-error message.
     */
    private suspend fun buildTransportErrorMessage(
        response: io.ktor.client.statement.HttpResponse,
    ): String {
        val statusMsg = "HTTP ${response.status.value}: ${response.status.description}"
        return try {
            val error = response.body<CloudErrorResponse>()
            val retryHint = when (error.retryable) {
                true -> " [retryable]"
                false -> " [non-retryable]"
                null -> ""
            }
            "$statusMsg (${error.errorCode}: ${error.message})$retryHint"
        } catch (_: Exception) {
            statusMsg
        }
    }

    override suspend fun checkVersion(
        agentVersion: String,
        bearerToken: String,
    ): CloudVersionCheckResult {
        return try {
            val response = httpClient.get("$cloudBaseUrl/api/v1/agent/version-check") {
                bearerAuth(bearerToken)
                // NET-005: Send both appVersion (preferred by backend) and agentVersion
                // for alignment with the cloud VersionCheckRequest contract.
                parameter("appVersion", agentVersion)
                parameter("agentVersion", agentVersion)
            }
            when (response.status) {
                HttpStatusCode.OK -> CloudVersionCheckResult.Success(response.body())
                HttpStatusCode.Unauthorized -> CloudVersionCheckResult.Unauthorized
                else -> {
                    CloudVersionCheckResult.TransportError(
                        buildTransportErrorMessage(response),
                    )
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            CloudVersionCheckResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    /**
     * M-02: When the hostname changes, rebuild the OkHttp client so certificate pins
     * are applied to the new hostname. Without this, a URL change to a different host
     * causes SSL handshake failures because the pinner remains bound to the old hostname.
     */
    override fun updateBaseUrl(newBaseUrl: String) {
        // NET-007: Synchronized to ensure cloudBaseUrl and httpClient are updated atomically.
        // Without this, a concurrent API call could read the new URL with the old client (stale pins)
        // or vice versa.
        synchronized(urlUpdateLock) {
            val resolved = resolveBaseUrl(newBaseUrl)
            val oldHost = extractHostname(cloudBaseUrl)
            val newHost = extractHostname(resolved)

            cloudBaseUrl = resolved

            if (certificatePins.isNotEmpty() && oldHost != null && newHost != null && oldHost != newHost) {
                AppLogger.i(TAG, "Hostname changed ($oldHost -> $newHost) — rebuilding HTTP client with new pins")
                val oldClient = httpClient
                httpClient = buildKtorClient(certificatePins, resolved, socketFactory)
                oldClient.close()
            } else {
                AppLogger.i(TAG, "Updating cloud base URL (hostname unchanged or no pins)")
            }
        }
    }

    override suspend fun uploadBatch(
        request: CloudUploadRequest,
        bearerToken: String,
    ): CloudUploadResult {
        return try {
            val response = httpClient.post("$cloudBaseUrl/api/v1/transactions/upload") {
                contentType(ContentType.Application.Json)
                bearerAuth(bearerToken)
                setBody(request)
            }
            when (response.status) {
                HttpStatusCode.OK -> CloudUploadResult.Success(response.body())
                HttpStatusCode.Unauthorized -> CloudUploadResult.Unauthorized
                HttpStatusCode.Forbidden -> {
                    val errorCode = try {
                        response.body<CloudErrorResponse>().errorCode
                    } catch (_: Exception) {
                        null
                    }
                    CloudUploadResult.Forbidden(errorCode)
                }
                HttpStatusCode.TooManyRequests -> {
                    val retryAfter = parseRetryAfterSeconds(response.headers["Retry-After"])
                    CloudUploadResult.RateLimited(retryAfter)
                }
                HttpStatusCode.PayloadTooLarge -> CloudUploadResult.PayloadTooLarge
                else -> {
                    CloudUploadResult.TransportError(
                        buildTransportErrorMessage(response),
                    )
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            CloudUploadResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    override suspend fun getSyncedStatus(
        since: String,
        bearerToken: String,
    ): CloudStatusPollResult {
        return try {
            val response = httpClient.get("$cloudBaseUrl/api/v1/transactions/synced-status") {
                bearerAuth(bearerToken)
                parameter("since", since)
            }
            when (response.status) {
                HttpStatusCode.OK -> CloudStatusPollResult.Success(response.body())
                HttpStatusCode.Unauthorized -> CloudStatusPollResult.Unauthorized
                HttpStatusCode.Forbidden -> {
                    val errorCode = try {
                        response.body<CloudErrorResponse>().errorCode
                    } catch (_: Exception) {
                        null
                    }
                    CloudStatusPollResult.Forbidden(errorCode)
                }
                HttpStatusCode.TooManyRequests -> {
                    val retryAfter = parseRetryAfterSeconds(response.headers["Retry-After"])
                    CloudStatusPollResult.RateLimited(retryAfter)
                }
                else -> {
                    CloudStatusPollResult.TransportError(
                        buildTransportErrorMessage(response),
                    )
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            CloudStatusPollResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    override suspend fun getConfig(
        currentConfigVersion: Int?,
        bearerToken: String,
    ): CloudConfigPollResult {
        return try {
            val response = httpClient.get("$cloudBaseUrl/api/v1/agent/config") {
                bearerAuth(bearerToken)
                if (currentConfigVersion != null) {
                    header(HttpHeaders.IfNoneMatch, "\"$currentConfigVersion\"")
                }
            }
            when (response.status) {
                HttpStatusCode.OK -> {
                    val rawJson = response.bodyAsText()
                    val etag = response.headers[HttpHeaders.ETag]
                    CloudConfigPollResult.Success(rawJson, etag)
                }
                HttpStatusCode.NotModified -> CloudConfigPollResult.NotModified
                HttpStatusCode.Unauthorized -> CloudConfigPollResult.Unauthorized
                HttpStatusCode.Forbidden -> {
                    val errorCode = try {
                        response.body<CloudErrorResponse>().errorCode
                    } catch (_: Exception) {
                        null
                    }
                    CloudConfigPollResult.Forbidden(errorCode)
                }
                HttpStatusCode.TooManyRequests -> {
                    val retryAfter = parseRetryAfterSeconds(response.headers["Retry-After"])
                    CloudConfigPollResult.RateLimited(retryAfter)
                }
                else -> {
                    CloudConfigPollResult.TransportError(
                        buildTransportErrorMessage(response),
                    )
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            CloudConfigPollResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    override suspend fun submitTelemetry(
        payload: TelemetryPayload,
        bearerToken: String,
    ): CloudTelemetryResult {
        return try {
            val response = httpClient.post("$cloudBaseUrl/api/v1/agent/telemetry") {
                contentType(ContentType.Application.Json)
                bearerAuth(bearerToken)
                setBody(payload)
            }
            when (response.status) {
                HttpStatusCode.NoContent -> CloudTelemetryResult.Success
                HttpStatusCode.Unauthorized -> CloudTelemetryResult.Unauthorized
                HttpStatusCode.Forbidden -> {
                    val errorCode = try {
                        response.body<CloudErrorResponse>().errorCode
                    } catch (_: Exception) {
                        null
                    }
                    CloudTelemetryResult.Forbidden(errorCode)
                }
                HttpStatusCode.TooManyRequests -> {
                    val retryAfter = parseRetryAfterSeconds(response.headers["Retry-After"])
                    CloudTelemetryResult.RateLimited(retryAfter)
                }
                else -> {
                    CloudTelemetryResult.TransportError(
                        buildTransportErrorMessage(response),
                    )
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            CloudTelemetryResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    override suspend fun forwardPreAuth(
        request: PreAuthForwardRequest,
        bearerToken: String,
    ): CloudPreAuthForwardResult {
        return try {
            val response = httpClient.post("$cloudBaseUrl/api/v1/preauth") {
                contentType(ContentType.Application.Json)
                bearerAuth(bearerToken)
                setBody(request)
            }
            when (response.status) {
                HttpStatusCode.Created,
                HttpStatusCode.OK -> CloudPreAuthForwardResult.Success(response.body())
                HttpStatusCode.Unauthorized -> CloudPreAuthForwardResult.Unauthorized
                HttpStatusCode.Forbidden -> {
                    val errorCode = try {
                        response.body<CloudErrorResponse>().errorCode
                    } catch (_: Exception) {
                        null
                    }
                    CloudPreAuthForwardResult.Forbidden(errorCode)
                }
                HttpStatusCode.TooManyRequests -> {
                    val retryAfter = parseRetryAfterSeconds(response.headers["Retry-After"])
                    CloudPreAuthForwardResult.RateLimited(retryAfter)
                }
                HttpStatusCode.Conflict -> {
                    val error = try {
                        response.body<CloudErrorResponse>()
                    } catch (_: Exception) {
                        null
                    }
                    CloudPreAuthForwardResult.Conflict(error?.errorCode, error?.message)
                }
                else -> {
                    CloudPreAuthForwardResult.TransportError(
                        buildTransportErrorMessage(response),
                    )
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            CloudPreAuthForwardResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    override suspend fun registerDevice(
        cloudBaseUrl: String,
        request: DeviceRegistrationRequest,
    ): CloudRegistrationResult {
        val registrationBaseUrl = cloudBaseUrl.trimEnd('/')
        val registrationClient = createPinnedRegistrationClient(registrationBaseUrl)
        val client = registrationClient ?: httpClient

        try {
            return try {
                val response = client.post("$registrationBaseUrl/api/v1/agent/register") {
                    contentType(ContentType.Application.Json)
                    setBody(request)
                }
                when (response.status) {
                    HttpStatusCode.Created -> CloudRegistrationResult.Success(response.body())
                    HttpStatusCode.BadRequest, HttpStatusCode.Conflict -> {
                        val error = try {
                            response.body<CloudErrorResponse>()
                        } catch (_: Exception) {
                            CloudErrorResponse("UNKNOWN", response.bodyAsText())
                        }
                        CloudRegistrationResult.Rejected(error.errorCode, error.message)
                    }
                    else -> {
                        CloudRegistrationResult.TransportError(
                            buildTransportErrorMessage(response),
                        )
                    }
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                CloudRegistrationResult.TransportError(e.message ?: "Unknown network error")
            }
        } finally {
            registrationClient?.close()
        }
    }

    override suspend fun refreshToken(refreshToken: String, deviceToken: String): CloudTokenRefreshResult {
        return try {
            val response = httpClient.post("$cloudBaseUrl/api/v1/agent/token/refresh") {
                contentType(ContentType.Application.Json)
                setBody(TokenRefreshRequest(refreshToken, deviceToken))
            }
            when (response.status) {
                HttpStatusCode.OK -> CloudTokenRefreshResult.Success(response.body())
                HttpStatusCode.Unauthorized -> CloudTokenRefreshResult.Unauthorized
                HttpStatusCode.Forbidden -> {
                    val errorCode = try {
                        response.body<CloudErrorResponse>().errorCode
                    } catch (_: Exception) {
                        null
                    }
                    CloudTokenRefreshResult.Forbidden(errorCode)
                }
                else -> {
                    CloudTokenRefreshResult.TransportError(
                        buildTransportErrorMessage(response),
                    )
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            CloudTokenRefreshResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    override suspend fun pollCommands(bearerToken: String): CloudCommandPollResult {
        return try {
            val response = httpClient.get("$cloudBaseUrl/api/v1/agent/commands") {
                bearerAuth(bearerToken)
            }
            when (response.status) {
                HttpStatusCode.OK -> CloudCommandPollResult.Success(response.body())
                HttpStatusCode.Unauthorized -> CloudCommandPollResult.Unauthorized
                HttpStatusCode.Forbidden -> {
                    val errorCode = try {
                        response.body<CloudErrorResponse>().errorCode
                    } catch (_: Exception) {
                        null
                    }
                    CloudCommandPollResult.Forbidden(errorCode)
                }
                HttpStatusCode.TooManyRequests -> {
                    val retryAfter = parseRetryAfterSeconds(response.headers["Retry-After"])
                    CloudCommandPollResult.RateLimited(retryAfter)
                }
                else -> CloudCommandPollResult.TransportError(
                    buildTransportErrorMessage(response),
                )
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            CloudCommandPollResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    override suspend fun ackCommand(
        commandId: String,
        request: CommandAckRequest,
        bearerToken: String,
    ): CloudCommandAckResult {
        return try {
            val response = httpClient.post("$cloudBaseUrl/api/v1/agent/commands/$commandId/ack") {
                contentType(ContentType.Application.Json)
                bearerAuth(bearerToken)
                setBody(request)
            }
            when (response.status) {
                HttpStatusCode.OK -> CloudCommandAckResult.Success(response.body())
                HttpStatusCode.Unauthorized -> CloudCommandAckResult.Unauthorized
                HttpStatusCode.Forbidden -> {
                    val errorCode = try {
                        response.body<CloudErrorResponse>().errorCode
                    } catch (_: Exception) {
                        null
                    }
                    CloudCommandAckResult.Forbidden(errorCode)
                }
                HttpStatusCode.Conflict -> {
                    val error = try {
                        response.body<CloudErrorResponse>()
                    } catch (_: Exception) {
                        null
                    }
                    CloudCommandAckResult.Conflict(error?.errorCode, error?.message)
                }
                else -> CloudCommandAckResult.TransportError(
                    buildTransportErrorMessage(response),
                )
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            CloudCommandAckResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    override suspend fun upsertAndroidInstallation(
        request: AndroidInstallationUpsertRequest,
        bearerToken: String,
    ): CloudInstallationUpsertResult {
        return try {
            val response = httpClient.post("$cloudBaseUrl/api/v1/agent/installations/android") {
                contentType(ContentType.Application.Json)
                bearerAuth(bearerToken)
                setBody(request)
            }
            when (response.status) {
                HttpStatusCode.NoContent -> CloudInstallationUpsertResult.Success
                HttpStatusCode.Unauthorized -> CloudInstallationUpsertResult.Unauthorized
                HttpStatusCode.Forbidden -> {
                    val errorCode = try {
                        response.body<CloudErrorResponse>().errorCode
                    } catch (_: Exception) {
                        null
                    }
                    CloudInstallationUpsertResult.Forbidden(errorCode)
                }
                else -> CloudInstallationUpsertResult.TransportError(
                    buildTransportErrorMessage(response),
                )
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            CloudInstallationUpsertResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    private fun createPinnedRegistrationClient(registrationBaseUrl: String): HttpClient? {
        val hostname = extractHostname(registrationBaseUrl)
            ?: throw IllegalArgumentException(
                "Cannot extract hostname from registration cloudBaseUrl '$registrationBaseUrl'",
            )
        if (isLoopbackHost(hostname)) {
            AppLogger.i(TAG, "Registration host $hostname is loopback - creating trust-all client (no pinning)")
            return buildKtorClient(emptyList(), registrationBaseUrl, socketFactory, skipPinFallback = true)
        }

        if (certificatePins.isEmpty()) return null

        AppLogger.i(TAG, "Using dedicated pinned registration client for $hostname")
        return registrationClientFactory?.invoke(registrationBaseUrl, certificatePins, socketFactory)
            ?: buildKtorClient(certificatePins, registrationBaseUrl, socketFactory)
    }

    override suspend fun submitDiagnosticLogs(
        request: DiagnosticLogUploadRequest,
        bearerToken: String,
    ): CloudDiagnosticLogResult {
        return try {
            val response = httpClient.post("$cloudBaseUrl/api/v1/agent/diagnostic-logs") {
                contentType(ContentType.Application.Json)
                bearerAuth(bearerToken)
                setBody(request)
            }
            when (response.status) {
                HttpStatusCode.NoContent, HttpStatusCode.OK -> CloudDiagnosticLogResult.Success
                HttpStatusCode.Unauthorized -> CloudDiagnosticLogResult.Unauthorized
                HttpStatusCode.Forbidden -> {
                    val errorCode = try {
                        response.body<CloudErrorResponse>().errorCode
                    } catch (_: Exception) { null }
                    CloudDiagnosticLogResult.Forbidden(errorCode)
                }
                else -> CloudDiagnosticLogResult.TransportError(
                    buildTransportErrorMessage(response),
                )
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            CloudDiagnosticLogResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    companion object {
        private const val TAG = "HttpCloudApiClient"

        /**
         * S-006: APK-bundled fallback certificate pins (SHA-256 SPKI hashes).
         *
         * These are used when no runtime pins have been delivered via SiteConfig yet
         * (e.g. initial device registration, first config poll). They close the
         * chicken-and-egg window where the device communicates with the cloud without
         * any pinning.
         *
         * Format: "sha256/BASE64==" — same as SiteConfig certificatePins.
         *
         * HOW TO UPDATE:
         *   1. Extract the pin from your TLS certificate:
         *        openssl x509 -in cert.pem -pubkey -noout \
         *          | openssl pkey -pubin -outform der \
         *          | openssl dgst -sha256 -binary \
         *          | base64
         *      then prepend "sha256/".
         *   2. Add both the primary certificate pin and a backup (next rotation cert).
         *   3. Mirror the same values in res/xml/network_security_config.xml.
         *   4. Remove expired pins after the rotation window has closed.
         *
         */
        val BUNDLED_PINS: List<String> = listOf(
            "sha256/YLh1dUR9y6Kja30RrAn7JKnbQG/uEtLMkBgFF2Fuihg=",
            "sha256/Vjs8r4z+80wjNcr1YKepWQboSIRi63WsWXhIMN+eWys=",
        )

        /**
         * Factory for the production Ktor client.
         *
         * Uses OkHttp engine (matches the rest of the Edge Agent) with
         * kotlinx-serialization-based JSON negotiation and lenient parsing
         * so unknown fields from future cloud API versions are ignored.
         *
         * @param cloudBaseUrl Base URL of the cloud API.
         * @param certificatePins Optional SHA-256 public key hashes for certificate pinning.
         *   Format: list of "sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=" strings.
         *   When non-empty, OkHttp will pin to these intermediate CA public keys.
         *   Per security spec §5.3, pins are delivered via SiteConfig from cloud,
         *   enabling rotation without APK update. On pin mismatch, the connection
         *   is refused (no fallback to unpinned).
         *   When empty, [BUNDLED_PINS] are used as a fallback so that initial
         *   registration is never left completely unpinned (S-006).
         */
        /** AT-016: Placeholder URL used before the device has registered. */
        const val PRE_REGISTRATION_URL = "https://not-yet-provisioned"

        fun create(
            cloudBaseUrl: String,
            certificatePins: List<String> = emptyList(),
            encryptedPrefsManager: EncryptedPrefsManager? = null,
            socketFactory: javax.net.SocketFactory? = null,
        ): HttpCloudApiClient {
            val client = buildKtorClient(certificatePins, cloudBaseUrl, socketFactory)
            return HttpCloudApiClient(cloudBaseUrl, client, encryptedPrefsManager, certificatePins, socketFactory)
        }

        /**
         * AT-016: Create a lightweight stub client for pre-registration.
         *
         * No certificate pinning is applied to the placeholder hostname itself.
         * [certificatePins] are still stored so [registerDevice] can create a
         * one-off pinned client for the QR hostname, and [updateBaseUrl] can
         * rebuild the long-lived client after registration provides a real host.
         */
        fun createPreRegistration(
            certificatePins: List<String>,
            encryptedPrefsManager: EncryptedPrefsManager? = null,
            socketFactory: javax.net.SocketFactory? = null,
        ): HttpCloudApiClient {
            AppLogger.i(TAG, "Creating pre-registration stub client (cert pinning deferred)")
            val client = buildKtorClient(emptyList(), PRE_REGISTRATION_URL, socketFactory, skipPinFallback = true)
            return HttpCloudApiClient(PRE_REGISTRATION_URL, client, encryptedPrefsManager, certificatePins, socketFactory)
        }

        /**
         * M-02: Extracted so updateBaseUrl can rebuild the client with new pins
         * when the hostname changes at runtime.
         */
        internal fun buildKtorClient(
            certificatePins: List<String>,
            cloudBaseUrl: String,
            socketFactory: javax.net.SocketFactory? = null,
            skipPinFallback: Boolean = false,
        ): HttpClient {
            return HttpClient(OkHttp) {
                engine {
                    // Phase 5: Attach X-Correlation-Id to every outbound request
                    addInterceptor { chain ->
                        val correlationId = AppLogger.correlationId ?: UUID.randomUUID().toString()
                        val request = chain.request().newBuilder()
                            .header("X-Correlation-Id", correlationId)
                            .build()
                        chain.proceed(request)
                    }

                    // S-006: merge runtime pins with APK-bundled fallback pins so that
                    // initial registration (before SiteConfig is available) is never unpinned.
                    // AT-016: skipPinFallback=true suppresses the BUNDLED_PINS fallback for
                    // the pre-registration stub client (no valid hostname to pin to).
                    val effectivePins = when {
                        certificatePins.isNotEmpty() -> certificatePins
                        skipPinFallback -> emptyList()
                        else -> BUNDLED_PINS
                    }

                    // Build certificate pinner if pins are configured
                    val certPinner = if (effectivePins.isNotEmpty()) {
                        // M-07: Fail-fast if hostname cannot be extracted. Silently disabling
                        // pinning on a malformed URL is a security degradation that must not
                        // go unnoticed — it is better to crash at startup than to send
                        // credentials over an unpinned connection.
                        val hostname = extractHostname(cloudBaseUrl)
                            ?: throw IllegalArgumentException(
                                "Cannot extract hostname from cloudBaseUrl '$cloudBaseUrl' — " +
                                    "certificate pinning requires a valid URL. Fix the URL or " +
                                    "remove certificate pins to proceed without pinning.",
                            )
                        val pinnerBuilder = CertificatePinner.Builder()
                        for (pin in effectivePins) {
                            pinnerBuilder.add(hostname, pin)
                        }
                        val source = if (certificatePins.isNotEmpty()) "runtime" else "bundled-fallback"
                        AppLogger.i(TAG, "Certificate pinning enabled for $hostname with ${effectivePins.size} pin(s) [$source]")
                        pinnerBuilder.build()
                    } else null

                    // Apply socket factory (for network binding) and cert pinner
                    // in a single config block — Ktor OkHttp engine only keeps the last one.
                    config {
                        // P-001: Explicit timeouts for field devices with variable connectivity.
                        // OkHttp defaults (10s each) are too short for slow-network upload bursts
                        // and block the sync cadence tick for up to 30s on unresponsive servers.
                        // connect: 15s covers slow LTE hand-off; read/write: 30s covers large
                        // batch payloads on throttled connections.
                        connectTimeout(15_000, TimeUnit.MILLISECONDS)
                        readTimeout(30_000, TimeUnit.MILLISECONDS)
                        writeTimeout(30_000, TimeUnit.MILLISECONDS)

                        // NET-013: Explicitly document OkHttp's default retry behavior.
                        // retryOnConnectionFailure(true) is the OkHttp default — transparent
                        // retry on connection reset / stale socket. Safe because all current
                        // endpoints are idempotent (upload has uploadBatchId, preauth has
                        // dedup-key, token refresh is idempotent). If non-idempotent endpoints
                        // are added in the future, set this to false or per-request.
                        retryOnConnectionFailure(true)

                        if (socketFactory != null) {
                            socketFactory(socketFactory)
                        }
                        if (certPinner != null) {
                            certificatePinner(certPinner)
                        }

                        // DEV-ONLY: Trust self-signed certs for emulator loopback host
                        val hostname = extractHostname(cloudBaseUrl)
                        if (hostname != null && isLoopbackHost(hostname)) {
                            val trustAllManager = object : X509TrustManager {
                                override fun checkClientTrusted(chain: Array<X509Certificate>?, authType: String?) = Unit
                                override fun checkServerTrusted(chain: Array<X509Certificate>?, authType: String?) = Unit
                                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
                            }
                            val sslContext = SSLContext.getInstance("TLS")
                            sslContext.init(null, arrayOf<TrustManager>(trustAllManager), SecureRandom())
                            sslSocketFactory(sslContext.socketFactory, trustAllManager)
                            hostnameVerifier { _, _ -> true }
                            AppLogger.i(TAG, "DEV: SSL verification disabled for loopback host $hostname")
                        }
                    }
                }
                // NET-011: isLenient removed — strict JSON parsing surfaces malformed
                // server responses as errors instead of silently accepting them.
                // ignoreUnknownKeys alone provides forward-compatibility.
                install(ContentNegotiation) {
                    json(
                        Json {
                            ignoreUnknownKeys = true
                        },
                    )
                }
            }
        }

        /**
         * M-15: Parse the Retry-After header value as seconds.
         * Accepts integer seconds (e.g. "30") or returns null for HTTP-date / missing / invalid.
         */
        internal fun parseRetryAfterSeconds(header: String?): Long? {
            if (header == null) return null
            return header.trim().toLongOrNull()?.takeIf { it > 0 }
        }

        /**
         * Extract the hostname from a URL for certificate pinning.
         * Supports wildcard matching: "api.fcc-middleware.prod.example.com" → "*.fcc-middleware.prod.example.com"
         */
        internal fun extractHostname(url: String): String? {
            return try {
                val withoutScheme = url.removePrefix("https://").removePrefix("http://")
                val host = withoutScheme.split("/").firstOrNull()?.split(":")?.firstOrNull()
                host?.takeIf { it.isNotBlank() }
            } catch (_: Exception) {
                null
            }
        }

        internal fun isLoopbackHost(host: String): Boolean {
            val normalized = host.lowercase()
            return normalized == "localhost" ||
                normalized == "127.0.0.1" ||
                normalized == "::1" ||
                normalized == "0:0:0:0:0:0:0:1" ||
                normalized == "10.0.2.2" // Android emulator host loopback
        }
    }
}

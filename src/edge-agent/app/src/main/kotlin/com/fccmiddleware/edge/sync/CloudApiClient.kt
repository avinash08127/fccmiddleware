package com.fccmiddleware.edge.sync

import io.ktor.client.HttpClient
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

// ---------------------------------------------------------------------------
// Status poll result type
// ---------------------------------------------------------------------------

sealed class CloudStatusPollResult {
    /** HTTP 200 — statuses returned for the requested IDs. */
    data class Success(val response: SyncedStatusResponse) : CloudStatusPollResult()

    /** HTTP 401 — access token expired or invalid; caller should refresh and retry. */
    data object Unauthorized : CloudStatusPollResult()

    /**
     * HTTP 403 — access forbidden.
     * [errorCode] == "DEVICE_DECOMMISSIONED" means the device has been deactivated.
     */
    data class Forbidden(val errorCode: String?) : CloudStatusPollResult()

    /** Network or non-2xx/401/403 failure. Retry on next cadence tick. */
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

    /** Network or non-2xx/401/403 failure. Retry on next cadence tick. */
    data class TransportError(val message: String) : CloudUploadResult()
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
     * Calls `GET /api/v1/transactions/synced-status?ids={comma-separated}`.
     *
     * @param fccTransactionIds FCC transaction IDs to check (max 500).
     * @param bearerToken Device JWT from [DeviceTokenProvider.getAccessToken].
     */
    suspend fun getSyncedStatus(
        fccTransactionIds: List<String>,
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
    private val cloudBaseUrl: String,
    private val httpClient: HttpClient,
) : CloudApiClient {

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
                else -> {
                    CloudUploadResult.TransportError(
                        "HTTP ${response.status.value}: ${response.status.description}",
                    )
                }
            }
        } catch (e: Exception) {
            CloudUploadResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    override suspend fun getSyncedStatus(
        fccTransactionIds: List<String>,
        bearerToken: String,
    ): CloudStatusPollResult {
        return try {
            val response = httpClient.get("$cloudBaseUrl/api/v1/transactions/synced-status") {
                bearerAuth(bearerToken)
                parameter("ids", fccTransactionIds.joinToString(","))
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
                else -> {
                    CloudStatusPollResult.TransportError(
                        "HTTP ${response.status.value}: ${response.status.description}",
                    )
                }
            }
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
                else -> {
                    CloudConfigPollResult.TransportError(
                        "HTTP ${response.status.value}: ${response.status.description}",
                    )
                }
            }
        } catch (e: Exception) {
            CloudConfigPollResult.TransportError(e.message ?: "Unknown network error")
        }
    }

    companion object {
        /**
         * Factory for the production Ktor client.
         *
         * Uses OkHttp engine (matches the rest of the Edge Agent) with
         * kotlinx-serialization-based JSON negotiation and lenient parsing
         * so unknown fields from future cloud API versions are ignored.
         */
        fun create(cloudBaseUrl: String): HttpCloudApiClient {
            val client = HttpClient(OkHttp) {
                install(ContentNegotiation) {
                    json(
                        Json {
                            ignoreUnknownKeys = true
                            isLenient = true
                        },
                    )
                }
            }
            return HttpCloudApiClient(cloudBaseUrl, client)
        }
    }
}

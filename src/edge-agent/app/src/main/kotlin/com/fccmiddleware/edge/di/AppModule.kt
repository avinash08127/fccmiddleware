package com.fccmiddleware.edge.di

import com.fccmiddleware.edge.api.LocalApiServer
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.buffer.CleanupWorker
import com.fccmiddleware.edge.buffer.IntegrityChecker
import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.preauth.PreAuthHandler
import com.fccmiddleware.edge.runtime.CadenceController
import com.fccmiddleware.edge.runtime.FccRuntimeState
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.CloudUploadWorker
import com.fccmiddleware.edge.sync.ConfigPollWorker
import com.fccmiddleware.edge.sync.DeviceTokenProvider
import com.fccmiddleware.edge.sync.HttpCloudApiClient
import com.fccmiddleware.edge.sync.PreAuthCloudForwardWorker
import com.fccmiddleware.edge.sync.KeystoreDeviceTokenProvider
import com.fccmiddleware.edge.adapter.common.FccAdapterFactory
import com.fccmiddleware.edge.adapter.common.IFccAdapterFactory
import com.fccmiddleware.edge.sync.TelemetryReporter
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import okhttp3.OkHttpClient
import okhttp3.Request
import org.koin.android.ext.koin.androidContext
import org.koin.dsl.module
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.TimeUnit

val appModule = module {

    // -------------------------------------------------------------------------
    // Shared service scope — SupervisorJob so child failures don't cancel siblings
    // -------------------------------------------------------------------------
    single<CoroutineScope> { CoroutineScope(SupervisorJob() + Dispatchers.IO) }

    // -------------------------------------------------------------------------
    // Database — single WAL-mode Room instance shared across all consumers
    // -------------------------------------------------------------------------
    single { BufferDatabase.create(androidContext()) }
    single { get<BufferDatabase>().transactionDao() }
    single { get<BufferDatabase>().preAuthDao() }
    single { get<BufferDatabase>().nozzleDao() }
    single { get<BufferDatabase>().syncStateDao() }
    single { get<BufferDatabase>().agentConfigDao() }
    single { get<BufferDatabase>().auditLogDao() }

    // -------------------------------------------------------------------------
    // Security — Keystore and EncryptedSharedPreferences
    // -------------------------------------------------------------------------
    single { KeystoreManager() }
    single { EncryptedPrefsManager(androidContext()) }
    single { ConfigManager(agentConfigDao = get()) }
    single { FccRuntimeState() }

    // -------------------------------------------------------------------------
    // Cloud API Client
    //
    // Created with the cloud base URL from EncryptedPrefs (set at registration).
    // Before registration, a default stub URL is used — the client is only called
    // after the device is provisioned. Registration calls pass the URL explicitly.
    // -------------------------------------------------------------------------
    single<CloudApiClient> {
        val encryptedPrefs = get<EncryptedPrefsManager>()
        val baseUrl = encryptedPrefs.cloudBaseUrl ?: "https://not-yet-provisioned"
        // Bootstrap pins bundled in the APK ensure certificate pinning is active
        // during device registration (before SiteConfig delivers runtime pins).
        // These are SHA-256 hashes of the intermediate CA public keys for the
        // known cloud endpoint(s). Update when rotating cloud TLS certificates.
        // TODO (EA-2.x): once SiteConfig delivers runtime pins, prefer those over bootstrap pins.
        val certificatePins = listOf(
            "sha256/YLh1dUR9y6Kja30RrAn7JKnbQG/uEtLMkBgFF2Fuihg=", // Primary intermediate CA
            "sha256/Vjs8r4z+80wjNcr1YKepWQboSIRi63WsWXhIMN+eWys=", // Backup intermediate CA
        )
        HttpCloudApiClient.create(baseUrl, certificatePins)
    }

    // -------------------------------------------------------------------------
    // Device Token Provider — Keystore-backed JWT + refresh token management
    // -------------------------------------------------------------------------
    single<DeviceTokenProvider> {
        KeystoreDeviceTokenProvider(
            keystoreManager = get(),
            encryptedPrefs = get(),
            cloudApiClient = get(),
        )
    }

    // -------------------------------------------------------------------------
    // Buffer management
    // -------------------------------------------------------------------------
    single { TransactionBufferManager(get()) }
    single { CleanupWorker(get(), get(), get(), androidContext()) }
    single { IntegrityChecker(get(), get(), androidContext()) }

    // -------------------------------------------------------------------------
    // Connectivity Manager
    //
    // Internet probe: HTTP GET to cloud /health with 4s timeout.
    // FCC probe: TCP socket connect to fccHost:fccPort with 4s timeout.
    //
    // Both probes read connection info from EncryptedPrefs on each invocation,
    // so they automatically start returning real results after registration
    // populates the cloud URL and FCC host/port.
    // -------------------------------------------------------------------------
    single {
        val encryptedPrefs = get<EncryptedPrefsManager>()
        val configManager = get<ConfigManager>()
        val runtimeState = get<FccRuntimeState>()
        val probeHttpClient = OkHttpClient.Builder()
            .connectTimeout(4, TimeUnit.SECONDS)
            .readTimeout(4, TimeUnit.SECONDS)
            .build()

        ConnectivityManager(
            internetProbe = {
                val baseUrl = configManager.config.value?.sync?.cloudBaseUrl ?: encryptedPrefs.cloudBaseUrl
                if (baseUrl.isNullOrBlank()) {
                    false
                } else {
                    try {
                        val request = Request.Builder()
                            .url("${baseUrl.trimEnd('/')}/health")
                            .get()
                            .build()
                        probeHttpClient.newCall(request).execute().use { it.isSuccessful }
                    } catch (_: Exception) {
                        false
                    }
                }
            },
            fccProbe = {
                val adapter = runtimeState.adapter ?: return@ConnectivityManager false
                try {
                    adapter.heartbeat()
                } catch (_: Exception) {
                    false
                }
            },
            auditLogDao = get(),
            listener = null, // CadenceController registers itself after construction
            scope = get<CoroutineScope>(),
        )
    }

    // -------------------------------------------------------------------------
    // Telemetry
    // -------------------------------------------------------------------------
    single {
        TelemetryReporter(
            context = androidContext(),
            transactionDao = get(),
            syncStateDao = get(),
            connectivityManager = get(),
            configManager = get(),
        )
    }

    // -------------------------------------------------------------------------
    // Workers — now wired with real CloudApiClient and DeviceTokenProvider
    // -------------------------------------------------------------------------
    single {
        CloudUploadWorker(
            bufferManager = get(),
            syncStateDao = get(),
            cloudApiClient = get(),
            tokenProvider = get(),
            telemetryReporter = get(),
        )
    }
    single {
        IngestionOrchestrator(
            bufferManager = get(),
            syncStateDao = get(),
        )
    }
    single {
        PreAuthHandler(
            preAuthDao = get(),
            nozzleDao = get(),
            connectivityManager = get(),
            auditLogDao = get(),
            scope = get<CoroutineScope>(),
        )
    }

    // -------------------------------------------------------------------------
    // Pre-Auth Cloud Forward Worker
    // -------------------------------------------------------------------------
    single {
        PreAuthCloudForwardWorker(
            preAuthDao = get(),
            cloudApiClient = get(),
            tokenProvider = get(),
        )
    }

    // -------------------------------------------------------------------------
    // FCC Adapter Factory — resolves vendor adapters at runtime from config
    // -------------------------------------------------------------------------
    single<IFccAdapterFactory> { FccAdapterFactory() }

    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------
    single {
        ConfigPollWorker(
            configManager = get(),
            syncStateDao = get(),
            cloudApiClient = get(),
            tokenProvider = get(),
        )
    }

    // -------------------------------------------------------------------------
    // Cadence Controller — single coalesced loop for all periodic resident work
    // -------------------------------------------------------------------------
    single {
        CadenceController(
            connectivityManager = get(),
            ingestionOrchestrator = get(),
            cloudUploadWorker = get(),
            transactionDao = get(),
            scope = get<CoroutineScope>(),
            preAuthHandler = get(),
            configPollWorker = get(),
            preAuthCloudForwardWorker = get(),
            tokenProvider = get(),
        )
    }

    // -------------------------------------------------------------------------
    // Local REST API Server
    // -------------------------------------------------------------------------
    single {
        val encryptedPrefs = get<EncryptedPrefsManager>()
        LocalApiServer(
            config = LocalApiServer.LocalApiServerConfig(
                port = 8585,
                enableLanApi = false,
                lanApiKey = null,
            ),
            transactionDao = get(),
            syncStateDao = get(),
            connectivityManager = get(),
            preAuthHandler = get(),
            serviceScope = get<CoroutineScope>(),
            ingestionOrchestrator = get(),
            deviceId = encryptedPrefs.deviceId ?: "00000000-0000-0000-0000-000000000000",
            siteCode = encryptedPrefs.siteCode ?: "UNPROVISIONED",
        )
    }
}

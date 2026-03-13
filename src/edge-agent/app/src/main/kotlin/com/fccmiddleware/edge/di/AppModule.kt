package com.fccmiddleware.edge.di

import com.fccmiddleware.edge.api.LocalApiServer
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.buffer.CleanupWorker
import com.fccmiddleware.edge.buffer.IntegrityChecker
import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.connectivity.BoundSocketFactory
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.connectivity.NetworkBinder
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.LocalOverrideManager
import com.fccmiddleware.edge.config.SiteDataManager
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
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.registration.RegistrationHandler
import com.fccmiddleware.edge.logging.LogLevel
import com.fccmiddleware.edge.logging.StructuredFileLogger
import com.fccmiddleware.edge.sync.TelemetryReporter
import com.fccmiddleware.edge.websocket.OdooWebSocketServer
import android.util.Log
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import okhttp3.OkHttpClient
import okhttp3.Request
import com.fccmiddleware.edge.ui.DiagnosticsViewModel
import com.fccmiddleware.edge.ui.ProvisioningViewModel
import org.koin.android.ext.koin.androidApplication
import org.koin.android.ext.koin.androidContext
import org.koin.androidx.viewmodel.dsl.viewModel
import org.koin.dsl.module
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.TimeUnit

val appModule = module {

    // -------------------------------------------------------------------------
    // Shared service scope — SupervisorJob so child failures don't cancel siblings.
    // AT-003: Single authoritative scope for the entire service lifecycle. The
    // foreground service injects this scope and cancels it in onDestroy(), so
    // all coroutines (workers, handlers, logger) stop together.
    // Exception handler logs to logcat directly; file logger is resolved lazily
    // to avoid a circular dependency (the logger itself uses a child of this scope).
    // -------------------------------------------------------------------------
    single<CoroutineScope> {
        val koin = getKoin()
        val exceptionHandler = CoroutineExceptionHandler { _, throwable ->
            Log.e("CoroutineScope", "Uncaught coroutine exception: ${throwable.message}", throwable)
            try {
                koin.get<StructuredFileLogger>().e(
                    "CoroutineScope",
                    "Uncaught coroutine exception: ${throwable.message}",
                    throwable,
                )
            } catch (_: Exception) {
                // File logger not yet initialized — logcat entry above is sufficient
            }
        }
        CoroutineScope(SupervisorJob() + Dispatchers.IO + exceptionHandler)
    }

    // -------------------------------------------------------------------------
    // Structured file logger — persistent JSONL logging (Phase 1A)
    // AT-004: Uses a child scope of the Koin-managed scope so it is cancelled
    // together with the service lifecycle (no orphaned scope).
    // -------------------------------------------------------------------------
    single {
        val parentScope = get<CoroutineScope>()
        StructuredFileLogger(
            context = androidContext(),
            scope = CoroutineScope(SupervisorJob(parentScope.coroutineContext[Job]) + Dispatchers.IO),
        )
    }

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
    single { get<BufferDatabase>().siteDataDao() }

    // -------------------------------------------------------------------------
    // Security — Keystore and EncryptedSharedPreferences
    // -------------------------------------------------------------------------
    single { KeystoreManager() }
    single { EncryptedPrefsManager(androidContext()) }
    single { ConfigManager(agentConfigDao = get(), keystoreManager = get(), encryptedPrefsManager = get()) }
    single { LocalOverrideManager(androidContext(), get()) }
    single { SiteDataManager(siteDataDao = get()) }
    single { FccRuntimeState() }

    // -------------------------------------------------------------------------
    // Cloud API Client
    //
    // AT-016: When registered, creates a full client with certificate pinning.
    // Before registration, creates a lightweight stub client without pinning
    // (no valid hostname to pin to). Certificate pins are stored so that
    // updateBaseUrl() can apply them after registration provides a real hostname.
    // -------------------------------------------------------------------------
    single<CloudApiClient> {
        val encryptedPrefs = get<EncryptedPrefsManager>()
        val baseUrl = encryptedPrefs.cloudBaseUrl
        val bootstrapPins = HttpCloudApiClient.BUNDLED_PINS
        val runtimePins = encryptedPrefs.runtimeCertificatePins
        val certificatePins = if (runtimePins.isNotEmpty()) {
            AppLogger.i("AppModule", "Using ${runtimePins.size} runtime certificate pin(s) from SiteConfig")
            runtimePins
        } else {
            AppLogger.i("AppModule", "Using ${bootstrapPins.size} bootstrap certificate pin(s)")
            bootstrapPins
        }
        val networkBinder = get<NetworkBinder>()
        val socketFactory = BoundSocketFactory { networkBinder.cloudNetwork.value }

        if (baseUrl != null) {
            HttpCloudApiClient.create(baseUrl, certificatePins, encryptedPrefs, socketFactory)
        } else {
            // AT-016: Pre-registration — no cert pinning for placeholder hostname.
            // Registration passes URL from QR code explicitly; updateBaseUrl() after
            // registration rebuilds the client with proper pins for the real hostname.
            HttpCloudApiClient.createPreRegistration(certificatePins, encryptedPrefs, socketFactory)
        }
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
    // Network Binder — tracks WiFi and mobile data networks as reactive state
    // -------------------------------------------------------------------------
    single { NetworkBinder(context = androidContext(), scope = get<CoroutineScope>()) }

    // -------------------------------------------------------------------------
    // Connectivity Manager
    //
    // Internet probe: HTTP GET to cloud /health with 4s timeout.
    //   Bound to cloudNetwork (mobile preferred, WiFi fallback).
    // FCC probe: adapter.heartbeat() — socket already bound to WiFi via T2.2.
    //
    // Both probes read connection info from EncryptedPrefs on each invocation,
    // so they automatically start returning real results after registration
    // populates the cloud URL and FCC host/port.
    // -------------------------------------------------------------------------
    single {
        val encryptedPrefs = get<EncryptedPrefsManager>()
        val configManager = get<ConfigManager>()
        val runtimeState = get<FccRuntimeState>()
        val networkBinderInstance = get<NetworkBinder>()

        // Internet probe OkHttp client bound to cloudNetwork (mobile > WiFi > default).
        val probeHttpClient = OkHttpClient.Builder()
            .socketFactory(BoundSocketFactory { networkBinderInstance.cloudNetwork.value })
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
            networkBinder = networkBinderInstance,
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
            fileLogger = get(),
            configManager = get(),
        )
    }
    single {
        IngestionOrchestrator(
            bufferManager = get(),
            syncStateDao = get(),
            transactionDao = get(),
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
    single<IFccAdapterFactory> { FccAdapterFactory(networkBinder = get()) }

    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------
    single {
        ConfigPollWorker(
            configManager = get(),
            syncStateDao = get(),
            cloudApiClient = get(),
            tokenProvider = get(),
            siteDataManager = get(),
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
            cloudApiClient = get(),
            agentVersion = androidContext().packageManager
                .getPackageInfo(androidContext().packageName, 0).versionName ?: "1.0.0",
            cleanupWorker = get(),
            fileLogger = get(),
        )
    }

    // -------------------------------------------------------------------------
    // Odoo WebSocket Server — backward-compat Fleck protocol
    // -------------------------------------------------------------------------
    single {
        OdooWebSocketServer(
            transactionDao = get(),
            serviceScope = get<CoroutineScope>(),
        )
    }

    // -------------------------------------------------------------------------
    // ViewModels (T-003)
    // -------------------------------------------------------------------------
    viewModel {
        DiagnosticsViewModel(
            connectivityManager = get(),
            siteDataDao = get(),
            transactionDao = get(),
            syncStateDao = get(),
            auditLogDao = get(),
            configManager = get(),
            fileLogger = get(),
        )
    }
    // AT-014: RegistrationHandler owns credential storage, config encryption,
    // and Room persistence — reusable outside the ViewModel context.
    single {
        RegistrationHandler(
            cloudApiClient = get(),
            keystoreManager = get(),
            encryptedPrefs = get(),
            agentConfigDao = get(),
            tokenProvider = get(),
            siteDataManager = get(),
            bufferDatabase = get(),
        )
    }
    viewModel {
        ProvisioningViewModel(
            application = androidApplication(),
            cloudApiClient = get(),
            encryptedPrefs = get(),
            registrationHandler = get(),
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
            configManager = get(),
            serviceScope = get<CoroutineScope>(),
            ingestionOrchestrator = get(),
            deviceId = encryptedPrefs.deviceId ?: "00000000-0000-0000-0000-000000000000",
            siteCode = encryptedPrefs.siteCode ?: "UNPROVISIONED",
        )
    }
}

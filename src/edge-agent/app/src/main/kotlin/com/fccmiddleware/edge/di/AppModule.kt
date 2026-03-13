package com.fccmiddleware.edge.di

import com.fccmiddleware.edge.api.LocalApiServer
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.buffer.BufferDatabaseFactory
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
import com.fccmiddleware.edge.logging.PlatformLogBridge
import com.fccmiddleware.edge.logging.StructuredFileLogger
import com.fccmiddleware.edge.sync.TelemetryReporter
import com.fccmiddleware.edge.websocket.OdooWebSocketServer
import androidx.security.crypto.MasterKey
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import com.fccmiddleware.edge.ui.DiagnosticsViewModel
import com.fccmiddleware.edge.ui.ProvisioningViewModel
import org.koin.android.ext.koin.androidApplication
import org.koin.android.ext.koin.androidContext
import org.koin.androidx.viewmodel.dsl.viewModel
import org.koin.dsl.module

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
            PlatformLogBridge.e("CoroutineScope", "Uncaught coroutine exception", throwable)
            try {
                koin.get<StructuredFileLogger>().e(
                    "CoroutineScope",
                    "Uncaught coroutine exception",
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
    single { BufferDatabaseFactory.create(androidContext(), get()) }
    single { get<BufferDatabase>().transactionDao() }
    single { get<BufferDatabase>().preAuthDao() }
    single { get<BufferDatabase>().nozzleDao() }
    single { get<BufferDatabase>().syncStateDao() }
    single { get<BufferDatabase>().agentConfigDao() }
    single { get<BufferDatabase>().auditLogDao() }
    single { get<BufferDatabase>().siteDataDao() }

    // -------------------------------------------------------------------------
    // Security — Keystore and EncryptedSharedPreferences
    // AP-031: Single shared MasterKey eliminates redundant Keystore IPC on startup.
    // -------------------------------------------------------------------------
    single { KeystoreManager() }
    single { MasterKey.Builder(androidContext()).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build() }
    single { EncryptedPrefsManager(androidContext(), get()) }
    single { ConfigManager(agentConfigDao = get(), keystoreManager = get(), encryptedPrefsManager = get()) }
    single { LocalOverrideManager(androidContext(), get(), get()) }
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
    single { TransactionBufferManager(get(), keystoreManager = get()) }
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
        // AP-029: Reuse CloudApiClient for the internet probe instead of a
        // separate OkHttpClient. This shares the connection pool (single TCP
        // connection to cloud host) and benefits from certificate pinning.
        val cloudApiClient = get<CloudApiClient>()

        ConnectivityManager(
            internetProbe = {
                val baseUrl = configManager.config.value?.sync?.cloudBaseUrl ?: encryptedPrefs.cloudBaseUrl
                if (baseUrl.isNullOrBlank()) {
                    false
                } else if (networkBinderInstance.cloudNetwork.value == null) {
                    false
                } else {
                    cloudApiClient.healthCheck()
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
            keystoreManager = get(),
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
            keystoreManager = get(),
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
            keystoreManager = get(),
            encryptedPrefsManager = get(),
        )
    }

    // -------------------------------------------------------------------------
    // Odoo WebSocket Server — backward-compat Fleck protocol
    // -------------------------------------------------------------------------
    single {
        OdooWebSocketServer(
            bufferManager = get(),
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
            localOverrideManager = get(),
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
            localOverrideManager = get(),
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
            bufferManager = get(),
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

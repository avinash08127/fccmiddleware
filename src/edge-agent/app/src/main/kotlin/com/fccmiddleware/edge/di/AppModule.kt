package com.fccmiddleware.edge.di

import com.fccmiddleware.edge.api.LocalApiServer
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.buffer.CleanupWorker
import com.fccmiddleware.edge.buffer.IntegrityChecker
import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.preauth.PreAuthHandler
import com.fccmiddleware.edge.runtime.CadenceController
import com.fccmiddleware.edge.sync.CloudUploadWorker
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import org.koin.android.ext.koin.androidContext
import org.koin.dsl.module

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
    // Buffer management
    // -------------------------------------------------------------------------
    single { TransactionBufferManager(get()) }
    single { CleanupWorker(get(), get(), get()) }
    single { IntegrityChecker(get(), get(), androidContext()) }

    // -------------------------------------------------------------------------
    // Connectivity Manager
    //
    // Internet probe: HTTP GET to cloud /health with 5s timeout.
    // FCC probe: calls IFccAdapter.heartbeat() with 5s timeout.
    //
    // Both probes are registered as no-op stubs until the adapter factory and
    // cloud URL are available from ConfigManager (EA-2.x). The agent starts in
    // FULLY_OFFLINE and will transition once probes return real results.
    // -------------------------------------------------------------------------
    single {
        ConnectivityManager(
            internetProbe = {
                // TODO (EA-2.x): replace with real HTTP GET to cloud GET /health
                // e.g.: httpClient.get(cloudHealthUrl).status.isSuccess()
                false
            },
            fccProbe = {
                // TODO (EA-2.x): replace with fccAdapterFactory.resolve(...).heartbeat()
                // The ConfigManager will update this probe once site config is loaded
                false
            },
            auditLogDao = get(),
            listener = null, // CadenceController registers itself after construction
            scope = get<CoroutineScope>(),
        )
    }

    // -------------------------------------------------------------------------
    // Workers
    // -------------------------------------------------------------------------
    single {
        CloudUploadWorker(
            bufferManager = get(),
            syncStateDao = get(),
            // cloudApiClient and tokenProvider require cloud URL and device JWT from the
            // security / config modules (EA-2.x). Injected as null stubs until then;
            // CloudUploadWorker safely no-ops when these are absent.
            cloudApiClient = null,   // TODO (EA-2.x): HttpCloudApiClient.create(cloudBaseUrl)
            tokenProvider = null,    // TODO (EA-2.x): inject EncryptedPrefsTokenProvider
        )
    }
    single {
        IngestionOrchestrator(
            adapter = null,          // TODO (EA-2.x): inject from adapter factory once wired
            bufferManager = get(),
            syncStateDao = get(),
            config = null,           // TODO (EA-2.x): inject from ConfigManager once wired
        )
    }
    single {
        PreAuthHandler(
            preAuthDao = get(),
            nozzleDao = get(),
            connectivityManager = get(),
            auditLogDao = get(),
            scope = get<CoroutineScope>(),
            fccAdapter = null, // TODO (EA-2.x): inject from adapter factory once wired
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
        )
    }

    // -------------------------------------------------------------------------
    // Local REST API Server
    // -------------------------------------------------------------------------
    single {
        LocalApiServer(
            config = LocalApiServer.LocalApiServerConfig(
                port = 8585,
                enableLanApi = false,     // TODO (EA-2.x): load from site config
                lanApiKey = null,
            ),
            transactionDao = get(),
            syncStateDao = get(),
            connectivityManager = get(),
            preAuthHandler = get(),
            fccAdapter = null,            // TODO (EA-2.x): resolve from adapter factory
            serviceScope = get<CoroutineScope>(),
            ingestionOrchestrator = get(),
        )
    }

    // TODO (EA-2.x): adapter factory
    // TODO (EA-2.x): config manager
}

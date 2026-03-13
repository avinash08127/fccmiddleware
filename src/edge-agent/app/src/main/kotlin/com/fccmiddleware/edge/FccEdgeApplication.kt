package com.fccmiddleware.edge

import android.app.Application
import com.fccmiddleware.edge.di.appModule
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.logging.StructuredFileLogger
import com.fccmiddleware.edge.buffer.TransactionBufferManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import org.koin.android.ext.koin.androidContext
import org.koin.core.context.startKoin
import org.koin.java.KoinJavaComponent.getKoin

class FccEdgeApplication : Application() {

    override fun onCreate() {
        super.onCreate()

        startKoin {
            androidContext(this@FccEdgeApplication)
            modules(appModule)
        }

        // Initialize AppLogger facade before anything else uses it
        val logger = getKoin().get<StructuredFileLogger>()
        AppLogger.init(logger)

        // Phase 1C: Global uncaught exception handler — writes crash to persistent log
        val defaultHandler = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, throwable ->
            try {
                logger.crash(
                    "UNCAUGHT_EXCEPTION",
                    "Uncaught exception on thread ${thread.name}",
                    throwable,
                )
            } catch (_: Exception) {
                // Best effort — avoid recursive crashes
            }
            // Delegate to default handler (system crash dialog / process kill)
            defaultHandler?.uncaughtException(thread, throwable)
        }

        logger.i("FccEdgeApplication", "FCC Edge Agent application started, crash handler installed")

        getKoin().get<CoroutineScope>().launch {
            try {
                getKoin().get<TransactionBufferManager>().migrateLegacyRawPayloads()
            } catch (e: Exception) {
                logger.e("FccEdgeApplication", "Failed to migrate legacy raw payload encryption", e)
            }
        }
    }
}

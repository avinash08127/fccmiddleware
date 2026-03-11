package com.fccmiddleware.edge

import android.app.Application
import android.util.Log
import com.fccmiddleware.edge.di.appModule
import org.koin.android.ext.koin.androidContext
import org.koin.core.context.startKoin

class FccEdgeApplication : Application() {

    override fun onCreate() {
        super.onCreate()
        Log.i("FccEdgeApplication", "FCC Edge Agent application starting")

        startKoin {
            androidContext(this@FccEdgeApplication)
            modules(appModule)
        }
    }
}

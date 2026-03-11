package com.fccmiddleware.agent

import org.koin.dsl.module

val appModule = module {
    // Database
    // single { AppDatabase.create(androidContext()) }

    // Adapters
    // single<IFccAdapter> { DomsAdapter(get()) }

    // Use cases
    // Ktor server
    // Connectivity manager
}

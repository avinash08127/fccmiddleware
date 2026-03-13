package com.fccmiddleware.edge.logging

import android.util.Log

/**
 * Bridges app logs to android.util.Log for local debugging only.
 *
 * Release builds keep diagnostics in StructuredFileLogger and avoid mirroring
 * app messages into logcat, which is readable over ADB on field devices.
 */
internal object PlatformLogBridge {
    private val isDebugBuild: Boolean by lazy {
        runCatching {
            Class.forName("com.fccmiddleware.edge.BuildConfig")
                .getField("DEBUG")
                .getBoolean(null)
        }.getOrDefault(false)
    }

    fun d(tag: String, msg: String) {
        if (!isDebugBuild) return
        Log.d(tag, LogSanitizer.sanitizeMessage(msg))
    }

    fun i(tag: String, msg: String) {
        if (!isDebugBuild) return
        Log.i(tag, LogSanitizer.sanitizeMessage(msg))
    }

    fun w(tag: String, msg: String) {
        if (!isDebugBuild) return
        Log.w(tag, LogSanitizer.sanitizeMessage(msg))
    }

    fun e(tag: String, msg: String, throwable: Throwable? = null) {
        if (!isDebugBuild) return

        val sanitizedMessage = LogSanitizer.sanitizeMessage(msg)
        if (throwable != null) {
            Log.e(tag, sanitizedMessage, throwable)
        } else {
            Log.e(tag, sanitizedMessage)
        }
    }
}

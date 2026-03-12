package com.fccmiddleware.edge.logging

import android.util.Log

/**
 * Static facade for [StructuredFileLogger] — enables `AppLogger.i(TAG, msg)` call-site
 * migration from `Log.i(TAG, msg)` without modifying every constructor in the codebase.
 *
 * Initialized in [FccEdgeApplication.onCreate] after Koin starts.
 * Before initialization, calls fall through to [android.util.Log] only.
 */
object AppLogger {

    @Volatile
    private var delegate: StructuredFileLogger? = null

    fun init(logger: StructuredFileLogger) {
        delegate = logger
    }

    fun d(tag: String, msg: String, extra: Map<String, String>? = null) {
        val l = delegate
        if (l != null) l.d(tag, msg, extra) else Log.d(tag, msg)
    }

    fun i(tag: String, msg: String, extra: Map<String, String>? = null) {
        val l = delegate
        if (l != null) l.i(tag, msg, extra) else Log.i(tag, msg)
    }

    fun w(tag: String, msg: String, extra: Map<String, String>? = null) {
        val l = delegate
        if (l != null) l.w(tag, msg, extra) else Log.w(tag, msg)
    }

    fun e(tag: String, msg: String, throwable: Throwable? = null, extra: Map<String, String>? = null) {
        val l = delegate
        if (l != null) l.e(tag, msg, throwable, extra)
        else if (throwable != null) Log.e(tag, msg, throwable)
        else Log.e(tag, msg)
    }

    /** Correlation ID for the current operation context. */
    var correlationId: String?
        get() = delegate?.correlationId
        set(value) { delegate?.correlationId = value }
}

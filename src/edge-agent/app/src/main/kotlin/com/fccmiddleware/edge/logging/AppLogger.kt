package com.fccmiddleware.edge.logging

import com.fccmiddleware.edge.security.SensitiveFieldFilter

/**
 * Static facade for [StructuredFileLogger] — enables `AppLogger.i(TAG, msg)` call-site
 * migration from `Log.i(TAG, msg)` without modifying every constructor in the codebase.
 *
 * Initialized in [FccEdgeApplication.onCreate] after Koin starts.
 * Before initialization, debug builds fall through to logcat; release builds
 * avoid mirroring app logs into logcat.
 */
object AppLogger {

    @Volatile
    private var delegate: StructuredFileLogger? = null

    fun init(logger: StructuredFileLogger) {
        delegate = logger
    }

    fun d(tag: String, msg: String, extra: Map<String, String>? = null) {
        delegate?.d(tag, msg, extra) ?: PlatformLogBridge.d(tag, msg)
    }

    fun i(tag: String, msg: String, extra: Map<String, String>? = null) {
        delegate?.i(tag, msg, extra) ?: PlatformLogBridge.i(tag, msg)
    }

    fun w(tag: String, msg: String, extra: Map<String, String>? = null) {
        delegate?.w(tag, msg, extra) ?: PlatformLogBridge.w(tag, msg)
    }

    fun e(tag: String, msg: String, throwable: Throwable? = null, extra: Map<String, String>? = null) {
        delegate?.e(tag, msg, throwable, extra) ?: PlatformLogBridge.e(tag, msg, throwable)
    }

    /**
     * Log an object using the runtime @Sensitive redactor so accidental object logging
     * never falls back to the raw Kotlin data-class toString().
     */
    fun redacted(tag: String, obj: Any?, extra: Map<String, String>? = null) {
        i(tag, obj?.let(SensitiveFieldFilter::redactToString) ?: "null", extra)
    }

    /** Correlation ID for the current operation context (thread-local, AF-002). */
    var correlationId: String?
        get() = delegate?.correlationId
        set(value) { delegate?.correlationId = value }

    /** Exposes the ThreadLocal for use with [CorrelationIdElement]. */
    val correlationIdLocal: ThreadLocal<String?>?
        get() = delegate?.correlationIdLocal
}

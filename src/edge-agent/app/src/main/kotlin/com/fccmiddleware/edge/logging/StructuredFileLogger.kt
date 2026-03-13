package com.fccmiddleware.edge.logging

import android.content.Context
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

/**
 * StructuredFileLogger — persistent JSONL logging for the Android Edge Agent.
 *
 * Writes structured log entries to `context.filesDir/logs/edge-agent-YYYY-MM-DD.jsonl`.
 * Each line is a self-contained JSON object:
 * ```json
 * {"ts":"2026-03-13T10:30:00.123Z","lvl":"INFO","tag":"CloudUploadWorker","msg":"Batch uploaded","cid":"abc-123","extra":{"batchSize":50}}
 * ```
 *
 * ## Design constraints (Urovo i9100, sub-Saharan Africa)
 * - Async buffered I/O via [Dispatchers.IO] + [Mutex] — never blocks the pre-auth hot path
 * - Rolling: 1 file/day, max [maxFiles] files (~10 MB cap)
 * - Bridges to [android.util.Log] simultaneously so ADB debugging still works
 * - Flushes immediately on ERROR level
 * - Respects [minLevel] from config (configurable at runtime via [updateLogLevel])
 */
class StructuredFileLogger(
    private val context: Context,
    private val scope: CoroutineScope,
    @Volatile var minLevel: LogLevel = LogLevel.INFO,
    private val maxFiles: Int = 5,
) {
    companion object {
        private const val LOG_DIR = "logs"
        private const val FILE_PREFIX = "edge-agent-"
        private const val FILE_SUFFIX = ".jsonl"
        private const val MAX_MESSAGE_LENGTH = 4000
        /** AP-023: Maximum entries retained in the in-memory diagnostic ring buffer. */
        private const val MAX_DIAGNOSTIC_BUFFER_SIZE = 200
    }

    private val logDir: File = File(context.filesDir, LOG_DIR).also { it.mkdirs() }
    private val writeMutex = Mutex()
    private val json = Json { encodeDefaults = false }

    @Volatile
    private var currentDate: LocalDate? = null

    @Volatile
    private var currentWriter: BufferedWriter? = null

    // AP-023: In-memory ring buffer of recent WARN/ERROR/FATAL log entries.
    // Populated from existing files on first access, then maintained incrementally
    // by writeEntry() and crash(). Eliminates file I/O from the 5-second diagnostic refresh.
    private val diagnosticBuffer = ArrayDeque<String>()
    private val diagnosticBufferLock = Any()
    @Volatile
    private var diagnosticBufferInitialized = false

    // AP-023: Cached total log size, invalidated on file rotation/deletion.
    @Volatile
    private var cachedLogSizeBytes: Long = -1L

    /**
     * Correlation ID attached to all log entries in the current context.
     *
     * AF-002: Uses a ThreadLocal so that concurrent Ktor request coroutines
     * (which may be pinned to different threads) do not overwrite each other's
     * correlation ID. Combined with [CorrelationIdElement] in the coroutine
     * context, the value is propagated correctly across coroutine dispatches.
     */
    private val correlationIdThreadLocal = ThreadLocal<String?>()

    var correlationId: String?
        get() = correlationIdThreadLocal.get()
        set(value) { correlationIdThreadLocal.set(value) }

    /** Exposes the ThreadLocal for use with [CorrelationIdElement]. */
    val correlationIdLocal: ThreadLocal<String?> get() = correlationIdThreadLocal

    // ── Public logging API ──────────────────────────────────────────────────

    fun d(tag: String, msg: String, extra: Map<String, String>? = null) {
        Log.d(tag, msg)
        writeEntry(LogLevel.DEBUG, tag, msg, extra)
    }

    fun i(tag: String, msg: String, extra: Map<String, String>? = null) {
        Log.i(tag, msg)
        writeEntry(LogLevel.INFO, tag, msg, extra)
    }

    fun w(tag: String, msg: String, extra: Map<String, String>? = null) {
        Log.w(tag, msg)
        writeEntry(LogLevel.WARN, tag, msg, extra)
    }

    fun e(tag: String, msg: String, throwable: Throwable? = null, extra: Map<String, String>? = null) {
        if (throwable != null) Log.e(tag, msg, throwable) else Log.e(tag, msg)
        val merged = buildMap {
            extra?.let { putAll(it) }
            throwable?.let {
                put("exception", it.javaClass.name)
                put("stackTrace", it.stackTraceToString().take(MAX_MESSAGE_LENGTH))
            }
        }
        writeEntry(LogLevel.ERROR, tag, msg, merged.ifEmpty { null })
    }

    /** Write a crash entry synchronously (called from uncaught exception handler). */
    fun crash(tag: String, msg: String, throwable: Throwable) {
        Log.e(tag, "CRASH: $msg", throwable)
        val entry = LogEntry(
            ts = Instant.now().toString(),
            lvl = "FATAL",
            tag = tag,
            msg = msg.take(MAX_MESSAGE_LENGTH),
            cid = correlationId,
            extra = buildJsonObject(mapOf(
                "exception" to throwable.javaClass.name,
                "stackTrace" to throwable.stackTraceToString().take(MAX_MESSAGE_LENGTH),
            )),
        )
        val jsonLine = json.encodeToString(entry)

        // AP-023: Add FATAL entry to diagnostic ring buffer
        synchronized(diagnosticBufferLock) {
            diagnosticBuffer.addLast(jsonLine)
            while (diagnosticBuffer.size > MAX_DIAGNOSTIC_BUFFER_SIZE) {
                diagnosticBuffer.removeFirst()
            }
        }

        // Synchronous write — we're about to die
        try {
            val writer = getOrCreateWriter()
            writer.write(jsonLine)
            writer.newLine()
            writer.flush()
        } catch (_: Exception) {
            // Best effort — can't log a failure to log
        }
    }

    // ── Log level control ───────────────────────────────────────────────────

    fun updateLogLevel(level: LogLevel) {
        minLevel = level
    }

    // ── Log retrieval (for diagnostics + upload) ────────────────────────────

    /**
     * Returns the most recent WARN/ERROR/FATAL entries, capped at [maxEntries].
     *
     * AP-023: Returns from an in-memory ring buffer (O(1)) instead of scanning
     * log files. The buffer is populated from existing files on first access,
     * then maintained incrementally by [writeEntry] and [crash].
     */
    fun getRecentDiagnosticEntries(maxEntries: Int = 200): List<String> {
        ensureDiagnosticBufferInitialized()
        synchronized(diagnosticBufferLock) {
            return diagnosticBuffer.takeLast(maxEntries)
        }
    }

    /**
     * AP-023: Lazily populate the diagnostic ring buffer from existing log files
     * so entries written before this process started are available immediately.
     * Uses double-checked locking to ensure one-time initialization.
     */
    private fun ensureDiagnosticBufferInitialized() {
        if (diagnosticBufferInitialized) return
        synchronized(diagnosticBufferLock) {
            if (diagnosticBufferInitialized) return
            try {
                val files = logDir.listFiles { f ->
                    f.name.startsWith(FILE_PREFIX) && f.name.endsWith(FILE_SUFFIX)
                }?.sortedByDescending { it.name }
                if (files != null) {
                    val entries = mutableListOf<String>()
                    for (file in files) {
                        try {
                            file.bufferedReader().useLines { lines ->
                                for (line in lines) {
                                    if (line.contains("\"lvl\":\"WARN\"") ||
                                        line.contains("\"lvl\":\"ERROR\"") ||
                                        line.contains("\"lvl\":\"FATAL\"")
                                    ) {
                                        entries.add(line)
                                    }
                                }
                            }
                        } catch (_: Exception) { /* skip corrupted files */ }
                    }
                    val start = (entries.size - MAX_DIAGNOSTIC_BUFFER_SIZE).coerceAtLeast(0)
                    for (i in start until entries.size) {
                        diagnosticBuffer.addLast(entries[i])
                    }
                }
            } catch (_: Exception) { /* best effort */ }
            diagnosticBufferInitialized = true
        }
    }

    /** Returns all log file paths for zip export. */
    fun getLogFiles(): List<File> =
        logDir.listFiles { f -> f.name.startsWith(FILE_PREFIX) && f.name.endsWith(FILE_SUFFIX) }
            ?.sortedByDescending { it.name }
            ?.toList()
            ?: emptyList()

    /**
     * Total size of all log files in bytes.
     *
     * AP-023: Returns a cached value that is invalidated on file rotation and
     * deletion, avoiding a directory listing + N file stats on every call.
     */
    fun totalLogSizeBytes(): Long {
        val cached = cachedLogSizeBytes
        if (cached >= 0L) return cached
        val size = getLogFiles().sumOf { it.length() }
        cachedLogSizeBytes = size
        return size
    }

    // ── Cleanup (called by CleanupWorker) ───────────────────────────────────

    fun rotateOldFiles() {
        val files = logDir.listFiles { f -> f.name.startsWith(FILE_PREFIX) && f.name.endsWith(FILE_SUFFIX) }
            ?.sortedByDescending { it.name }
            ?: return

        if (files.size > maxFiles) {
            files.drop(maxFiles).forEach { file ->
                file.delete()
            }
            // AP-023: Invalidate cached log size after deleting files
            cachedLogSizeBytes = -1L
        }
    }

    fun flush() {
        try {
            currentWriter?.flush()
        } catch (_: Exception) {
            // Best effort
        }
    }

    /**
     * AT-004: Flush buffered entries and close the underlying writer.
     * Called by the foreground service in onDestroy() before scope cancellation
     * to ensure the last log entries are persisted to disk.
     */
    fun close() {
        try {
            currentWriter?.flush()
            currentWriter?.close()
            currentWriter = null
            currentDate = null
        } catch (_: Exception) {
            // Best effort — process is shutting down
        }
    }

    // ── Internal ────────────────────────────────────────────────────────────

    private fun writeEntry(level: LogLevel, tag: String, msg: String, extra: Map<String, String>?) {
        if (level.severity < minLevel.severity) return

        val entry = LogEntry(
            ts = Instant.now().toString(),
            lvl = level.name,
            tag = tag,
            msg = msg.take(MAX_MESSAGE_LENGTH),
            cid = correlationId,
            extra = extra?.let { buildJsonObject(it) },
        )

        val jsonLine = json.encodeToString(entry)

        // AP-023: Append WARN/ERROR/FATAL entries to in-memory ring buffer so
        // getRecentDiagnosticEntries() returns in O(1) without file I/O.
        if (level.severity >= LogLevel.WARN.severity) {
            synchronized(diagnosticBufferLock) {
                diagnosticBuffer.addLast(jsonLine)
                while (diagnosticBuffer.size > MAX_DIAGNOSTIC_BUFFER_SIZE) {
                    diagnosticBuffer.removeFirst()
                }
            }
        }

        val flushNow = level.severity >= LogLevel.ERROR.severity

        scope.launch(Dispatchers.IO) {
            writeMutex.withLock {
                try {
                    val writer = getOrCreateWriter()
                    writer.write(jsonLine)
                    writer.newLine()
                    if (flushNow) writer.flush()
                } catch (e: Exception) {
                    // Can't log a failure to log — avoid infinite recursion
                    Log.e("StructuredFileLogger", "Failed to write log entry", e)
                }
            }
        }
    }

    private fun getOrCreateWriter(): BufferedWriter {
        val today = LocalDate.now(ZoneOffset.UTC)
        if (today != currentDate) {
            currentWriter?.let {
                try { it.flush(); it.close() } catch (_: Exception) {}
            }
            val file = File(logDir, "$FILE_PREFIX${today.format(DateTimeFormatter.ISO_LOCAL_DATE)}$FILE_SUFFIX")
            currentWriter = BufferedWriter(FileWriter(file, true))
            currentDate = today
            rotateOldFiles()
            // AP-023: Invalidate cached log size on file rotation
            cachedLogSizeBytes = -1L
        }
        return currentWriter!!
    }

    private fun buildJsonObject(map: Map<String, String>): JsonObject =
        JsonObject(map.mapValues { (_, v) -> JsonPrimitive(v) })

    @Serializable
    private data class LogEntry(
        val ts: String,
        val lvl: String,
        val tag: String,
        val msg: String,
        val cid: String? = null,
        val extra: JsonObject? = null,
    )
}

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
    }

    private val logDir: File = File(context.filesDir, LOG_DIR).also { it.mkdirs() }
    private val writeMutex = Mutex()
    private val json = Json { encodeDefaults = false }

    @Volatile
    private var currentDate: LocalDate? = null

    @Volatile
    private var currentWriter: BufferedWriter? = null

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
        // Synchronous write — we're about to die
        try {
            val writer = getOrCreateWriter()
            writer.write(json.encodeToString(entry))
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

    /** Returns recent WARN/ERROR entries for remote upload, capped at [maxEntries]. */
    fun getRecentDiagnosticEntries(maxEntries: Int = 200): List<String> {
        val entries = mutableListOf<String>()
        val files = logDir.listFiles { f -> f.name.startsWith(FILE_PREFIX) && f.name.endsWith(FILE_SUFFIX) }
            ?.sortedByDescending { it.name }
            ?: return entries

        for (file in files) {
            if (entries.size >= maxEntries) break
            try {
                file.bufferedReader().useLines { lines ->
                    for (line in lines) {
                        if (entries.size >= maxEntries) return@useLines
                        if (line.contains("\"lvl\":\"WARN\"") || line.contains("\"lvl\":\"ERROR\"") || line.contains("\"lvl\":\"FATAL\"")) {
                            entries.add(line)
                        }
                    }
                }
            } catch (_: Exception) {
                // Skip corrupted files
            }
        }
        return entries.takeLast(maxEntries)
    }

    /** Returns all log file paths for zip export. */
    fun getLogFiles(): List<File> =
        logDir.listFiles { f -> f.name.startsWith(FILE_PREFIX) && f.name.endsWith(FILE_SUFFIX) }
            ?.sortedByDescending { it.name }
            ?.toList()
            ?: emptyList()

    /** Total size of all log files in bytes. */
    fun totalLogSizeBytes(): Long =
        getLogFiles().sumOf { it.length() }

    // ── Cleanup (called by CleanupWorker) ───────────────────────────────────

    fun rotateOldFiles() {
        val files = logDir.listFiles { f -> f.name.startsWith(FILE_PREFIX) && f.name.endsWith(FILE_SUFFIX) }
            ?.sortedByDescending { it.name }
            ?: return

        if (files.size > maxFiles) {
            files.drop(maxFiles).forEach { file ->
                file.delete()
            }
        }
    }

    fun flush() {
        try {
            currentWriter?.flush()
        } catch (_: Exception) {
            // Best effort
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

        val flushNow = level.severity >= LogLevel.ERROR.severity

        scope.launch(Dispatchers.IO) {
            writeMutex.withLock {
                try {
                    val writer = getOrCreateWriter()
                    writer.write(json.encodeToString(entry))
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

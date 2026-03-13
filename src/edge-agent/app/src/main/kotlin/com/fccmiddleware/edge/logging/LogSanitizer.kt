package com.fccmiddleware.edge.logging

import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/**
 * Sanitizes diagnostic log content before persistence or export.
 *
 * Keeps operationally useful context while removing LAN topology, cloud endpoints,
 * raw identifiers, and verbose call-chain details from stack traces.
 */
object LogSanitizer {

    const val REDACTED_HOST = "***"
    const val REDACTED_URL = "[REDACTED_URL]"
    const val REDACTED_VALUE = "[REDACTED]"
    const val REDACTED_PORT = "[REDACTED]"

    private val json = Json { prettyPrint = false }

    private val assignmentRegex = Regex("""\b([A-Za-z][A-Za-z0-9._-]{0,40})\s*=\s*([^,\s)]+)""")
    private val urlRegex = Regex("""https?://[^\s)\],]+""", RegexOption.IGNORE_CASE)
    private val ipv4Regex = Regex("""\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?\b""")
    private val uuidRegex = Regex(
        """\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b""",
    )
    private val hostnameChangeRegex =
        Regex("""\(([A-Za-z0-9.-]+\.[A-Za-z]{2,}) -> ([A-Za-z0-9.-]+\.[A-Za-z]{2,})\)""")

    fun sanitizeMessage(message: String): String = sanitizeFreeText(message)

    fun sanitizeExtra(extra: Map<String, String>): Map<String, String> =
        extra.mapValues { (key, value) -> sanitizeValue(key, value) }

    fun sanitizeStackTrace(stackTrace: String): String {
        val summary = stackTrace
            .lineSequence()
            .firstOrNull { it.isNotBlank() }
            ?.trim()
            ?: REDACTED_VALUE
        return sanitizeFreeText(summary)
    }

    fun sanitizeExceptionClassName(clazz: Class<*>): String =
        clazz.simpleName.takeIf { it.isNotBlank() } ?: clazz.name.substringAfterLast('.')

    fun sanitizeJsonLine(line: String): String {
        val trimmed = line.trim()
        if (trimmed.isEmpty()) return line

        return runCatching {
            val element = json.parseToJsonElement(trimmed)
            json.encodeToString(JsonElement.serializer(), sanitizeElement(element))
        }.getOrElse {
            sanitizeFreeText(line)
        }
    }

    private fun sanitizeElement(element: JsonElement, keyHint: String? = null): JsonElement =
        when (element) {
            is JsonObject -> JsonObject(element.mapValues { (key, value) -> sanitizeElement(value, key) })
            is JsonArray -> JsonArray(element.map { sanitizeElement(it, keyHint) })
            is JsonPrimitive -> sanitizePrimitive(element, keyHint)
        }

    private fun sanitizePrimitive(primitive: JsonPrimitive, keyHint: String?): JsonElement {
        if (primitive.isString) {
            return JsonPrimitive(sanitizeValue(keyHint, primitive.content))
        }
        return when {
            keyHint.isPortKey() -> JsonPrimitive(0)
            else -> primitive
        }
    }

    private fun sanitizeValue(keyHint: String?, value: String): String {
        val normalizedKey = keyHint?.lowercase()
        return when {
            normalizedKey == null -> sanitizeFreeText(value)
            normalizedKey.contains("stacktrace") -> sanitizeStackTrace(value)
            normalizedKey == "exception" -> value.substringAfterLast('.')
            normalizedKey.contains("deviceid") -> redactIdentifier(value)
            normalizedKey.isHostKey() -> REDACTED_HOST
            normalizedKey.isPortKey() -> REDACTED_PORT
            normalizedKey.isUrlKey() -> REDACTED_URL
            normalizedKey.isSensitiveKey() -> REDACTED_VALUE
            else -> sanitizeFreeText(value)
        }
    }

    private fun sanitizeFreeText(input: String): String {
        var sanitized = input

        sanitized = assignmentRegex.replace(sanitized) { match ->
            val key = match.groupValues[1]
            val value = match.groupValues[2]
            sanitizeAssignment(key, value) ?: match.value
        }

        sanitized = hostnameChangeRegex.replace(sanitized, "(*** -> ***)")
        sanitized = urlRegex.replace(sanitized, REDACTED_URL)
        sanitized = ipv4Regex.replace(sanitized, REDACTED_HOST)
        sanitized = uuidRegex.replace(sanitized) { redactIdentifier(it.value) }

        return sanitized
    }

    private fun sanitizeAssignment(key: String, value: String): String? {
        val normalizedKey = key.lowercase()
        val redactedValue = when {
            normalizedKey.contains("deviceid") -> redactIdentifier(value)
            normalizedKey.isHostKey() -> REDACTED_HOST
            normalizedKey.isPortKey() -> REDACTED_PORT
            normalizedKey.isUrlKey() -> REDACTED_URL
            normalizedKey.isSensitiveKey() -> REDACTED_VALUE
            else -> return null
        }
        return "$key=$redactedValue"
    }

    private fun redactIdentifier(value: String): String =
        if (value.length <= 8) value else "${value.take(8)}..."

    private fun String?.isHostKey(): Boolean =
        this != null && (contains("host") || contains("hostname"))

    private fun String?.isPortKey(): Boolean =
        this != null && contains("port")

    private fun String?.isUrlKey(): Boolean =
        this != null && (contains("url") || contains("endpoint"))

    private fun String?.isSensitiveKey(): Boolean =
        this != null && (
            contains("token") ||
                contains("secret") ||
                contains("credential") ||
                contains("password") ||
                contains("accesscode") ||
                contains("taxid") ||
                contains("apikey")
            )
}

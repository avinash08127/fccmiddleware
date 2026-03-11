package com.fccmiddleware.edge.security

import kotlin.reflect.full.memberProperties
import kotlin.reflect.jvm.javaField

/**
 * Utility for redacting sensitive fields when serializing objects to log output.
 *
 * Inspects all Kotlin member properties for the [@Sensitive] annotation and replaces
 * their values with "[REDACTED]" in the output map. For JWT-like tokens (property names
 * containing "token" or "jwt", case-insensitive), the last 8 characters are preserved
 * as "...aBcDeFgH" per the security spec §5.4.
 *
 * Thread-safe. Reflection results are cached per class.
 *
 * Usage:
 * ```kotlin
 * Log.i(TAG, "Request: ${SensitiveFieldFilter.redact(request)}")
 * ```
 */
object SensitiveFieldFilter {

    private const val REDACTED = "[REDACTED]"
    private const val JWT_SUFFIX_LENGTH = 8

    /**
     * Returns a map representation of [obj] with sensitive fields redacted.
     *
     * - Fields annotated with [@Sensitive] → "[REDACTED]"
     * - JWT/token fields annotated with [@Sensitive] → "...last8chars"
     * - All other fields → their toString() value
     */
    fun redact(obj: Any): Map<String, Any?> {
        val klass = obj::class
        val result = mutableMapOf<String, Any?>()

        for (prop in klass.memberProperties) {
            val name = prop.name
            val isSensitive = prop.javaField?.isAnnotationPresent(Sensitive::class.java) == true ||
                prop.annotations.any { it is Sensitive }

            val rawValue = try {
                prop.getter.call(obj)
            } catch (_: Exception) {
                null
            }

            result[name] = if (isSensitive) {
                redactValue(name, rawValue)
            } else {
                rawValue
            }
        }
        return result
    }

    /**
     * Redact a single value based on the field name hint.
     *
     * Per security spec §5.4:
     *   - Device JWT: log last 8 chars only → "...aBcDeFgH"
     *   - All other sensitive fields: "[REDACTED]"
     */
    private fun redactValue(fieldName: String, value: Any?): String {
        if (value == null) return REDACTED
        val str = value.toString()
        val isTokenField = fieldName.contains("token", ignoreCase = true) ||
            fieldName.contains("jwt", ignoreCase = true)
        return if (isTokenField && str.length > JWT_SUFFIX_LENGTH) {
            "...${str.takeLast(JWT_SUFFIX_LENGTH)}"
        } else {
            REDACTED
        }
    }

    /**
     * Convenience: return a redacted string representation of [obj].
     */
    fun redactToString(obj: Any): String {
        return redact(obj).entries.joinToString(", ", "{", "}") { (k, v) -> "$k=$v" }
    }
}

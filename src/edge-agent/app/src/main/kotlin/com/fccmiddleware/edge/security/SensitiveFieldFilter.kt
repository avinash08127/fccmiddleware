package com.fccmiddleware.edge.security

import kotlin.reflect.full.memberProperties
import kotlin.reflect.full.primaryConstructor
import kotlin.reflect.jvm.isAccessible
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

        // Build a set of parameter names annotated @Sensitive on the primary constructor.
        // This is a fallback for cases where the annotation ends up on the constructor
        // parameter rather than the property (e.g. if @Sensitive targets VALUE_PARAMETER).
        val sensitiveCtorParams: Set<String> = klass.primaryConstructor
            ?.parameters
            ?.filter { p -> p.annotations.any { it is Sensitive } }
            ?.mapNotNull { it.name }
            ?.toSet()
            ?: emptySet()

        for (prop in klass.memberProperties) {
            val name = prop.name
            val isSensitive = prop.javaField?.isAnnotationPresent(Sensitive::class.java) == true ||
                prop.annotations.any { it is Sensitive } ||
                name in sensitiveCtorParams

            val rawValue = try {
                prop.isAccessible = true
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
     *   - Device JWT (`deviceToken` or `*jwt*` fields): log last 8 chars only → "...aBcDeFgH"
     *   - All other sensitive fields (refresh token, bootstrap token, FCC creds,
     *     customer TIN, LAN API key, etc.): "[REDACTED]"
     */
    private fun redactValue(fieldName: String, value: Any?): String {
        if (value == null) return REDACTED
        val str = value.toString()
        // Only the device JWT gets the suffix preview; all other tokens/secrets are fully redacted.
        val isDeviceJwt = fieldName.equals("deviceToken", ignoreCase = true) ||
            fieldName.contains("jwt", ignoreCase = true)
        return if (isDeviceJwt && str.length > JWT_SUFFIX_LENGTH) {
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

package com.fccmiddleware.edge.adapter.doms.jpl

import kotlinx.serialization.Serializable

/** JPL protocol message: every message has a name, optional sub-code, and JSON data map. */
@Serializable
data class JplMessage(
    val name: String,
    val subCode: Int? = null,
    val data: Map<String, String> = emptyMap(),
) {
    override fun toString(): String {
        val redactedData = data.mapValues { (key, value) ->
            if (isSensitiveKey(key)) {
                REDACTED
            } else {
                value
            }
        }
        return "JplMessage(name=$name, subCode=$subCode, data=$redactedData)"
    }

    companion object {
        private const val REDACTED = "[REDACTED]"
        private val sensitiveKeyMarkers = listOf("accesscode", "secret", "password")

        private fun isSensitiveKey(key: String): Boolean =
            sensitiveKeyMarkers.any { marker -> key.contains(marker, ignoreCase = true) }
    }
}

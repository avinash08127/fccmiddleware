package com.fccmiddleware.edge.config

/**
 * Built-in cloud environment map.
 * v2 QR codes reference an environment key (e.g. "PRODUCTION") instead of a raw URL.
 */
object CloudEnvironments {

    data class CloudEnv(val baseUrl: String, val displayName: String)

    val ENVIRONMENTS: Map<String, CloudEnv> = mapOf(
        "PRODUCTION" to CloudEnv("https://api.fccmiddleware.io", "Production"),
        "STAGING" to CloudEnv("https://api-staging.fccmiddleware.io", "Staging"),
        "DEVELOPMENT" to CloudEnv("https://api-dev.fccmiddleware.io", "Development"),
        "LOCAL" to CloudEnv("https://localhost:5001", "Local"),
    )

    /** Resolve an environment key to its base URL, or null if unknown. */
    fun resolve(env: String?): String? {
        if (env.isNullOrBlank()) return null
        return ENVIRONMENTS[env.uppercase()]?.baseUrl
    }

    /** All environment keys in display order. */
    val keys: List<String> get() = listOf("PRODUCTION", "STAGING", "DEVELOPMENT", "LOCAL")

    /** Display names in the same order as [keys]. */
    val displayNames: List<String> get() = keys.map { ENVIRONMENTS[it]!!.displayName }
}

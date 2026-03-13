package com.fccmiddleware.edge.adapter.common

import java.net.URI

/**
 * Resolves FCC HTTP endpoints with secure-by-default rules for LAN traffic.
 *
 * Non-loopback FCC endpoints must use HTTPS. Cleartext HTTP remains allowed only
 * for loopback/local simulator addresses because that traffic never leaves the
 * device boundary.
 */
internal object FccTransportSecurity {

    data class HttpEndpoint(
        val scheme: String,
        val host: String,
        val port: Int,
        val basePath: String,
    ) {
        fun asBaseUrl(): String {
            val normalizedPath = basePath.trimEnd('/').ifBlank { "/" }
            return URI(scheme, null, host, port, normalizedPath, null, null)
                .toString()
                .trimEnd('/')
        }

        fun resolve(path: String): String {
            val normalizedBasePath = basePath.trimEnd('/')
            val normalizedPath = if (path.startsWith("/")) path else "/$path"
            val fullPath = "$normalizedBasePath$normalizedPath"
            return URI(scheme, null, host, port, fullPath, null, null).toString()
        }
    }

    fun resolveEndpoint(
        rawAddress: String,
        component: String,
        defaultPort: Int,
    ): HttpEndpoint {
        val uri = normalizeHttpUri(
            rawValue = rawAddress,
            component = component,
            explicitPort = defaultPort,
        )
        return HttpEndpoint(
            scheme = requireNotNull(uri.scheme).lowercase(),
            host = requireNotNull(uri.host),
            port = uri.port,
            basePath = uri.rawPath ?: "",
        )
    }

    fun resolveAbsoluteUrl(rawUrl: String, component: String): String =
        normalizeHttpUri(
            rawValue = rawUrl,
            component = component,
            explicitPort = null,
        ).toString()

    private fun normalizeHttpUri(
        rawValue: String,
        component: String,
        explicitPort: Int?,
    ): URI {
        val trimmed = rawValue.trim()
        require(trimmed.isNotBlank()) { "$component endpoint is blank" }

        val candidate = if (trimmed.contains("://")) {
            trimmed
        } else {
            "${defaultSchemeForBareAddress(trimmed)}://$trimmed"
        }

        val parsed = URI(candidate)
        val scheme = parsed.scheme?.lowercase()
            ?: throw IllegalArgumentException("$component endpoint is missing a URL scheme")
        require(scheme == "http" || scheme == "https") {
            "$component endpoint must use http or https, got '$scheme'"
        }

        val host = parsed.host
            ?: throw IllegalArgumentException("$component endpoint is missing a host")
        if (scheme == "http" && !isLoopbackHost(host)) {
            throw IllegalArgumentException(
                "$component cleartext HTTP is not allowed for non-loopback FCC endpoints; configure HTTPS",
            )
        }

        val port = when {
            parsed.port != -1 -> parsed.port
            explicitPort != null -> explicitPort
            scheme == "https" -> 443
            else -> 80
        }

        return URI(
            scheme,
            parsed.userInfo,
            host,
            port,
            parsed.rawPath ?: null,
            parsed.rawQuery,
            parsed.rawFragment,
        )
    }

    private fun defaultSchemeForBareAddress(rawAddress: String): String =
        if (isLoopbackHost(extractHost(rawAddress))) "http" else "https"

    private fun extractHost(rawAddress: String): String {
        val authority = rawAddress.substringBefore('/')
        return when {
            authority.startsWith("[") -> authority.substringAfter('[').substringBefore(']')
            authority.count { it == ':' } > 1 -> authority
            else -> authority.substringBefore(':')
        }
    }

    private fun isLoopbackHost(host: String): Boolean {
        val normalized = host.lowercase()
        return normalized == "localhost" ||
            normalized == "127.0.0.1" ||
            normalized == "::1" ||
            normalized == "0:0:0:0:0:0:0:1"
    }
}

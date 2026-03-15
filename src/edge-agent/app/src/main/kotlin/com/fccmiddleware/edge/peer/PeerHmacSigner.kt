package com.fccmiddleware.edge.peer

import java.security.MessageDigest
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import java.time.Duration
import java.time.OffsetDateTime
import java.time.format.DateTimeParseException

/**
 * HMAC-SHA256 request signing for peer-to-peer authentication.
 * Produces identical signatures to the C# PeerHmacSigner for the same inputs.
 */
object PeerHmacSigner {

    private val MAX_CLOCK_DRIFT: Duration = Duration.ofSeconds(30)

    /**
     * Computes HMAC-SHA256 signature over the canonical request string.
     * Format: "{method}\n{path}\n{timestamp}\n{bodyHash}"
     */
    fun sign(secret: String, method: String, path: String, timestamp: String, body: String?): String {
        val bodyHash = computeBodyHash(body)
        val canonical = "${method.uppercase()}\n$path\n$timestamp\n$bodyHash"

        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(secret.toByteArray(Charsets.UTF_8), "HmacSHA256"))
        val hash = mac.doFinal(canonical.toByteArray(Charsets.UTF_8))
        return hash.joinToString("") { "%02x".format(it) }
    }

    /**
     * Verifies an incoming request's HMAC signature with clock drift tolerance.
     */
    fun verify(secret: String, method: String, path: String, timestamp: String, body: String?, signature: String): Boolean {
        // Validate timestamp drift
        val requestTime = try {
            OffsetDateTime.parse(timestamp)
        } catch (_: DateTimeParseException) {
            return false
        }

        val drift = Duration.between(requestTime.toInstant(), java.time.Instant.now())
        if (drift.abs() > MAX_CLOCK_DRIFT) return false

        val expected = sign(secret, method, path, timestamp, body)

        // Constant-time comparison
        return MessageDigest.isEqual(
            expected.toByteArray(Charsets.UTF_8),
            signature.toByteArray(Charsets.UTF_8),
        )
    }

    private fun computeBodyHash(body: String?): String {
        if (body.isNullOrEmpty()) {
            return "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" // SHA-256 of empty
        }
        val digest = MessageDigest.getInstance("SHA-256")
        return digest.digest(body.toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02x".format(it) }
    }
}

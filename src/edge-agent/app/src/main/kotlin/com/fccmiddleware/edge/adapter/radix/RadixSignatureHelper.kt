package com.fccmiddleware.edge.adapter.radix

import java.security.MessageDigest

/**
 * Radix SHA-1 message signing utility.
 *
 * Every Radix FCC request and response is signed. The signature is a SHA-1 hash of
 * the relevant XML element content concatenated **immediately** with the shared secret
 * password — no separator, no trailing newline.
 *
 * **Signing scope by port:**
 * - **Transaction management (port P+1):** `SHA1(<REQ>...</REQ> + SECRET_PASSWORD)`
 *   The hash covers the full `<REQ>` element including its opening and closing tags.
 *   Result is placed in the `<SIGNATURE>` sibling element inside `<HOST_REQ>`.
 *
 * - **External authorization / pre-auth (port P):** `SHA1(<AUTH_DATA>...</AUTH_DATA> + SECRET_PASSWORD)`
 *   The hash covers the full `<AUTH_DATA>` element including its tags.
 *   Result is placed in the `<FDCSIGNATURE>` sibling element inside `<FDCMS>`.
 *
 * **Critical:** Whitespace and special characters in the XML content matter —
 * the hash must match character-for-character. Do NOT trim, normalize, or reformat
 * the XML before signing. The FDC validates signatures and returns RESP_CODE=251
 * (SIGNATURE_ERR) on mismatch.
 */
object RadixSignatureHelper {

    /**
     * Computes the SHA-1 signature for a transaction management request (port P+1).
     *
     * The input [reqContent] must be the complete `<REQ>...</REQ>` element as it will
     * appear in the `<HOST_REQ>` XML body, including the opening `<REQ>` and closing
     * `</REQ>` tags. The [sharedSecret] is concatenated immediately after `</REQ>`
     * with no separator.
     *
     * @param reqContent Full `<REQ>...</REQ>` XML element (tags included, not trimmed)
     * @param sharedSecret The shared secret password configured for this FCC
     * @return Lowercase hex SHA-1 hash (40 characters)
     */
    fun computeTransactionSignature(reqContent: String, sharedSecret: String): String {
        val payload = reqContent + sharedSecret
        return sha1Hex(payload)
    }

    /**
     * Computes the SHA-1 signature for an external authorization / pre-auth request (port P).
     *
     * The input [authDataContent] must be the complete `<AUTH_DATA>...</AUTH_DATA>` element
     * as it will appear in the `<FDCMS>` XML body, including the opening `<AUTH_DATA>` and
     * closing `</AUTH_DATA>` tags. The [sharedSecret] is concatenated immediately after
     * `</AUTH_DATA>` with no separator.
     *
     * @param authDataContent Full `<AUTH_DATA>...</AUTH_DATA>` XML element (tags included, not trimmed)
     * @param sharedSecret The shared secret password configured for this FCC
     * @return Lowercase hex SHA-1 hash (40 characters)
     */
    fun computeAuthSignature(authDataContent: String, sharedSecret: String): String {
        val payload = authDataContent + sharedSecret
        return sha1Hex(payload)
    }

    /**
     * Validates a signature received in an FDC response.
     *
     * Recomputes the SHA-1 hash of [content] + [sharedSecret] and compares it
     * (case-insensitive) against the [expectedSignature] from the response.
     *
     * For transaction responses (`<FDC_RESP>`), [content] is the `<TABLE>...</TABLE>` element.
     * For pre-auth responses (`<FDCMS>`), [content] is the `<FDCACK>...</FDCACK>` element.
     *
     * @param content The XML element content that was signed (tags included, not trimmed)
     * @param expectedSignature The signature value from the response's `<SIGNATURE>` or `<FDCSIGNATURE>` element
     * @param sharedSecret The shared secret password configured for this FCC
     * @return `true` if the computed signature matches [expectedSignature], `false` otherwise
     */
    fun validateSignature(content: String, expectedSignature: String, sharedSecret: String): Boolean {
        val computed = sha1Hex(content + sharedSecret)
        return computed.equals(expectedSignature, ignoreCase = true)
    }

    /**
     * Computes SHA-1 hash of [input] and returns lowercase hex string (40 chars).
     *
     * The input is encoded as UTF-8 bytes before hashing. No trimming or normalization
     * is performed — the caller is responsible for providing the exact content.
     */
    private fun sha1Hex(input: String): String {
        val digest = MessageDigest.getInstance("SHA-1")
        val hashBytes = digest.digest(input.toByteArray(Charsets.UTF_8))
        return hashBytes.joinToString("") { "%02x".format(it) }
    }
}

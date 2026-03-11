package com.fccmiddleware.edge.security

/**
 * Marks a field as sensitive for log redaction purposes.
 *
 * Fields annotated with @Sensitive are automatically redacted by [SensitiveFieldFilter]
 * when objects are serialized to log output. The field value is replaced with "[REDACTED]".
 *
 * Per security spec §5.4, the following fields must never appear in logs:
 *   - FCC API key, FCC credentials (password, cert)
 *   - Device JWT (log last 8 chars only: ...aBcDeFgH)
 *   - Refresh token, bootstrap token, LAN API key, Odoo API key
 *   - Customer TIN (taxIdentificationNumber)
 *
 * Usage:
 * ```kotlin
 * data class TokenRefreshRequest(
 *     @Sensitive val refreshToken: String,
 * )
 * ```
 */
@Target(AnnotationTarget.PROPERTY, AnnotationTarget.FIELD)
@Retention(AnnotationRetention.RUNTIME)
@MustBeDocumented
annotation class Sensitive
